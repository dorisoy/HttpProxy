using System.Net.Mime;
using System.Text;

namespace HttpProxyApi
{
    public class UpdateAdress
    {
        public string Ip { get; set; }

        public string Id { get; set; }
    }
    
    public class Program
    {
        static string adress= "0.0.0.0";
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddAuthorization();

            var app = builder.Build();



            app.MapGet("/server", () => Results.Extensions.Html(
                 string.Format("<script>location.replace(\"http://{0}/\")</script>",adress+":20012")));

            app.MapPost("/ip", (UpdateAdress request) =>
            {
                System.Diagnostics.Debug.WriteLine(request.Ip);
                adress = request.Ip;
            });

            app.MapGet("/ip", ()=>Results.Bytes(ConvertIpToBytes(adress)));
            app.MapGet("", ()=>"hi->"+adress);
            app.Run();
        }
        private static byte[] ConvertIpToBytes(string ip)
        {
            string[] ipStringChunks = ip.Split('.');
            byte[] bytes = new byte[4];

            for (int i = 0; i < 4; i++)
            {
                bytes[i] = byte.Parse(ipStringChunks[i]);
            }
            return bytes;
        }

        
    }
}