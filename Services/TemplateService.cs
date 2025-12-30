using MiniPrinter;    // 引用你的打印机驱动命名空间
using System.Drawing; // 需要引用 System.Drawing.Common
using System.Text.Json;
using static MiniPrinter.Components.Pages.Home;

namespace MiniPrint.Services
{
    public class TemplateService
    {
        private readonly string _templateFolderPath;
        private readonly IMiniPrinterService _printerService;

        public TemplateService(IWebHostEnvironment env, IMiniPrinterService printerService)
        {
            _printerService = printerService;
            _templateFolderPath = Path.Combine(env.ContentRootPath, "Templates");
            if (!Directory.Exists(_templateFolderPath))
            {
                Directory.CreateDirectory(_templateFolderPath);
            }
        }

        public async Task<List<SavedTemplate>> GetListAsync()
        {
            var list = new List<SavedTemplate>();
            var files = Directory.GetFiles(_templateFolderPath, "*.json");

            foreach (var file in files)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    var item = await JsonSerializer.DeserializeAsync<SavedTemplate>(stream);
                    if (item != null) list.Add(item);
                }
                catch { /* 忽略损坏文件 */ }
            }
            return list.OrderByDescending(x => x.CreatedAt).ToList();
        }


        public async Task SaveAsync(SavedTemplate template)
        {
            string filePath = Path.Combine(_templateFolderPath, $"{template.Id}.json");
            string jsonContent = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, jsonContent);
        }

        public async Task UpdateAsync(SavedTemplate template)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
            {
                throw new ArgumentException("模板 ID 不能为空");
            }

            string filePath = Path.Combine(_templateFolderPath, $"{template.Id}.json");

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"未找到 ID 为 {template.Id} 的模板，无法更新。");
            }

            template.CreatedAt = DateTime.Now;

            string jsonContent = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, jsonContent);
        }

        public void Delete(string id)
        {
            string filePath = Path.Combine(_templateFolderPath, $"{id}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

    }
}