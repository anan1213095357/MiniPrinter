using Microsoft.AspNetCore.Mvc;
using MiniPrint.Services;
using MiniPrinter.Components;
using Photino.NET;
using System.Drawing;

namespace MiniPrinter
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddSingleton<IMiniPrinterService, MiniPrinterService>();
            builder.Services.AddControllers();
            builder.Services.AddSingleton<TemplateService>();
            HttpClient client = new();
            builder.Services.AddSingleton(client);
            var app = builder.Build();
            app.UseExceptionHandler("/Error");
            app.UseStaticFiles();
            app.UseAntiforgery();
            app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
            app.MapControllers();
            app.RunAsync("http://0.0.0.0:12134");
            Thread.Sleep(200);
            new PhotinoWindow().SetTitle("MiniPrinter - Ð¡ÖÇÑ§³¤").SetSize(new Size(1280, 720)).SetFullScreen(false).Center().Load("http://localhost:12134").WaitForClose();
            app.StopAsync().Wait();

        }
    }
}
