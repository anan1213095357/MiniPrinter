using Microsoft.AspNetCore.Mvc;
using MiniPrint.Services;
using MiniPrinter;
using QRCoder;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using static MiniPrinter.Components.Pages.Home; // 需要引用 System.Text.Json 用于序列化
using SkiaSharp;
namespace MiniPrint.Controllers
{
    [Route("api")]
    [ApiController]
    public class MiniPrintController : ControllerBase
    {
        // 定义模板存储文件夹路径
        private readonly string _templateFolderPath;
        private readonly IMiniPrinterService _printerService;
        private readonly IWebHostEnvironment env;
        private readonly TemplateService templateService;

        public MiniPrintController(IMiniPrinterService printerService, IWebHostEnvironment env, TemplateService templateService)
        {
            _printerService = printerService;
            this.env = env;
            this.templateService = templateService;
            // 在 ContentRoot 下创建一个 Templates 文件夹
            _templateFolderPath = Path.Combine(env.ContentRootPath, "Templates");

            // 如果文件夹不存在，自动创建
            if (!Directory.Exists(_templateFolderPath))
            {
                Directory.CreateDirectory(_templateFolderPath);
            }
        }

        // [保持原样] 静态打印接口
        [HttpPost("print")]
        public async Task<IActionResult> Print([FromBody] PrintRequest request)
        {
            if (string.IsNullOrEmpty(request.ImageBase64))
            {
                return BadRequest("无图片数据");
            }

            try
            {
                var base64Data = request.ImageBase64;
                if (base64Data.Contains(","))
                {
                    base64Data = base64Data.Split(',')[1];
                }

                byte[] imageBytes = Convert.FromBase64String(base64Data);
                using var ms = new MemoryStream(imageBytes);

                // 读取原始图片
                using var originalBitmap = new Bitmap(ms);

                // 如果前端传了尺寸，就调整图片大小
                Bitmap finalBitmap = originalBitmap;

                if (request.Width > 0 && request.Height > 0)
                {
                    finalBitmap = new Bitmap(originalBitmap, new Size(request.Width, request.Height));
                }

                try
                {
                    await _printerService.PrintAsync(finalBitmap);
                }
                finally
                {
                    if (finalBitmap != originalBitmap)
                    {
                        finalBitmap.Dispose();
                    }
                }

                return Ok(new { msg = "打印指令已发送" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"API Error: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // [修改] 动态打印接口 - 改为从磁盘读取文件
        [HttpPost("print/dynamic")]
        public async Task<IActionResult> PrintDynamic([FromBody] DynamicPrintRequest request)
        {
            // 1. 从磁盘读取模板
            string filePath = Path.Combine(_templateFolderPath, $"{request.TemplateId}.json");

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound($"找不到 ID 为 {request.TemplateId} 的模板文件");
            }

            SavedTemplate templateDto;
            try
            {
                string json = await System.IO.File.ReadAllTextAsync(filePath);
                templateDto = JsonSerializer.Deserialize<SavedTemplate>(json ?? "{}")!;
            }
            catch
            {
                return StatusCode(500, "模板文件读取或解析失败");
            }

            var layout = templateDto?.Data;
            if (layout == null) return BadRequest("模板数据损坏");

            // 2. 以下逻辑完全保持您原有的绘图代码不变
            try
            {
                using var bitmap = new Bitmap(layout.CanvasWidth, layout.CanvasHeight);
                using var g = Graphics.FromImage(bitmap);
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                foreach (var item in layout.Elements)
                {
                    // 坐标转换
                    float x = (float)item.X;
                    float y = (float)item.Y;
                    float w = (float)item.Width;
                    float h = (float)item.Height;
                    var state = g.Save();

                    float centerX = x + w / 2;
                    float centerY = y + h / 2;

                    if (Math.Abs(item.Rotation) > 0.1)
                    {
                        g.TranslateTransform(centerX, centerY);
                        g.RotateTransform((float)item.Rotation);
                        g.TranslateTransform(-centerX, -centerY);
                    }

                    // 获取动态值
                    string valueToPrint = item.Content;

                    if (request.Data != null &&
                        !string.IsNullOrEmpty(item.Content) &&
                        request.Data.TryGetValue(item.Content, out string dynamicVal))
                    {
                        valueToPrint = dynamicVal;
                    }

                    switch (item.Type)
                    {
                        case ElementType.Text:
                            using (var font = new Font("Arial", item.FontSize, FontStyle.Bold))
                            using (var brush = new SolidBrush(Color.Black))
                            {
                                g.DrawString(item.Content, font, brush, x, y);
                            }
                            break;

                        case ElementType.Image:
                            try
                            {
                                if (!string.IsNullOrEmpty(item.Content))
                                {
                                    // 2. 去掉路径开头可能存在的 '/' 或 '\'，防止 Path.Combine 失效
                                    item.Content = item.Content.TrimStart('/', '\\');

                                    // 3. 组合完整的物理路径：wwwroot 绝对路径 + 图片相对路径
                                    string fullPath = Path.Combine(env.WebRootPath, item.Content);

                                    // 4. 检查文件是否存在
                                    if (System.IO.File.Exists(fullPath))
                                    {
                                        byte[] imageBytes = await System.IO.File.ReadAllBytesAsync(fullPath);
                                        using var skBitmap = SKBitmap.Decode(imageBytes);
                                        using var skImage = SKImage.FromBitmap(skBitmap);
                                        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
                                        using var pngStream = new MemoryStream(data.ToArray());
                                        using var sourceImg = Image.FromStream(pngStream);
                                        g.DrawImage(sourceImg, new RectangleF(x, y, w, h));
                                    }
                                    else
                                    {
                                        // 文件不存在时的处理（画个红叉或者打印日志）
                                        throw new FileNotFoundException($"图片文件未找到: {fullPath}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // 错误处理：在画布上画个框提示
                                using (var errorPen = new Pen(Color.Black, 2))
                                {
                                    g.DrawRectangle(errorPen, x, y, w, h);
                                    // 字体改小一点防止溢出
                                    g.DrawString("Img Missing", new Font("Arial", 6), Brushes.Black, x, y);
                                }
                                // 记录日志，方便排查是哪个路径错了
                                Console.WriteLine($"图片加载失败. 路径: {valueToPrint}, 错误: {ex.Message}");
                            }
                            break;

                        case ElementType.QRCode:
                            using (var qrBmp = GenerateQrBitmap(item.Content))
                            {
                                g.DrawImage(qrBmp, new RectangleF(x, y, w, h));
                            }
                            break;

                        case ElementType.DynamicString:
                            using (var font = new Font("Arial", item.FontSize, FontStyle.Bold))
                            using (var brush = new SolidBrush(Color.Black))
                            {
                                g.DrawString(valueToPrint, font, brush, x, y);
                            }
                            break;

                        case ElementType.DynamicQRCode:
                            using (var dynQrBmp = GenerateQrBitmap(valueToPrint))
                            {
                                g.DrawImage(dynQrBmp, new RectangleF(x, y, w, h));
                            }
                            break;
                    }

                    g.Restore(state);
                }

                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                byte[] byteImage = ms.ToArray();

                // 如果需要实际打印，请取消注释并调用 _printerService

                //bitmap.Save("C:\\Users\\anan1\\Pictures\\新建文件夹 (7)\\test.bmp");


                await _printerService.PrintAsync(bitmap);

                return Ok(new { message = "动态打印成功", processedKeys = request.Data?.Keys, size = byteImage.Length });
            }
            catch (Exception ex)
            {
                return StatusCode(500, "生成失败: " + ex.Message);
            }
        }

        // [保持原样] 辅助方法
        private Bitmap GenerateQrBitmap(string content)
        {
            if (string.IsNullOrEmpty(content)) content = " ";
            using (var qrGenerator = new QRCodeGenerator())
            {
                using (var qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q))
                {
                    using (var qrCode = new QRCode(qrCodeData))
                    {
                        return qrCode.GetGraphic(20);
                    }
                }
            }
        }
    }

    // ================= DTO 类定义 (保持不变) =================

    public class PrintRequest
    {
        public string ImageBase64 { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class DynamicPrintRequest
    {
        public string TemplateId { get; set; }
        public Dictionary<string, string> Data { get; set; }
    }


    public class TemplateDataModel
    {
        public int CanvasWidth { get; set; }
        public int CanvasHeight { get; set; }
        public List<DesignElement> Elements { get; set; }
    }

}