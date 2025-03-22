using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

class Program
{
    private static readonly HttpClient client = new HttpClient();
    private const string ApiBaseUrl = "https://api.zenfolio.com/api/1.8/zfapi.asmx";

    static async Task<string> AuthenticateAsync(string loginName, string password)
    {
        // Compact SOAP envelope with loginName and password
        var soapRequest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:zf=\"http://www.zenfolio.com/api/1.8\"><soap:Body><zf:AuthenticatePlain><zf:loginName>{loginName}</zf:loginName><zf:password>{password}</zf:password></zf:AuthenticatePlain></soap:Body></soap:Envelope>";

        var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "http://www.zenfolio.com/api/1.8/AuthenticatePlain");

        // Add headers to mimic a browser and avoid Cloudflare issues
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/xml");

        // Log request details
        Console.WriteLine("Request URL: " + ApiBaseUrl);
        Console.WriteLine("Request Body: " + soapRequest);
        Console.WriteLine("SOAPAction: " + content.Headers.GetValues("SOAPAction").FirstOrDefault());
        Console.WriteLine("Content-Type: " + content.Headers.ContentType);

        try
        {
            var response = await client.PostAsync(ApiBaseUrl, content);
            var responseString = await response.Content.ReadAsStringAsync();
            Console.WriteLine("Status Code: " + response.StatusCode);
            Console.WriteLine("Response Body: " + responseString);

            if (response.IsSuccessStatusCode)
            {
                var xmlDoc = XDocument.Parse(responseString);
                var tokenElement = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}AuthenticatePlainResult").FirstOrDefault();
                if (tokenElement != null)
                {
                    return tokenElement.Value;
                }
                throw new Exception("No token found in response");
            }
            else
            {
                throw new HttpRequestException($"Server returned {response.StatusCode}: {responseString}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            throw;
        }
    }

    static async Task Main(string[] args)
    {
        try
        {
            string token = await AuthenticateAsync("jeromechboyer@gmail.com", "Rosalie15!");
            Console.WriteLine($"Token: {token}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Main Error: {ex.Message}");
        }
    }
}