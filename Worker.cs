using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeroconf;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace PrinterCommunicate
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private HttpListener _httpListener;
        private readonly HttpClient _httpClient;

        // The port your JavaScript frontend will communicate with locally
        private const string LISTENER_URL = "http://localhost:8081/";

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(LISTENER_URL);

            try
            {
                _httpListener.Start();
                _logger.LogInformation("Scanner Proxy Windows Service active and listening on: {Url}", LISTENER_URL);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to initialize the HTTP listener socket on port 8080. Ensure port is free.");
                return;
            }

            // Main execution loop running inside the Windows Service container
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await _httpListener.GetContextAsync();

                    // Offload each request to a detached background thread to keep UI interaction responsive
                    _ = Task.Run(() => HandleIncomingBrowserRequestAsync(context), stoppingToken);
                }
                catch (HttpListenerException) when (stoppingToken.IsCancellationRequested)
                {
                    // Clean exit when service is stopped via Windows SCM
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing an incoming request loop connection context.");
                }
            }
        }

        private async Task HandleIncomingBrowserRequestAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            // CORS HEADERS: Instructs the browser that your JavaScript app is authorized to make this call
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            // Handle browser CORS preflight checks automatically
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                response.Close();
                return;
            }
            //route 1
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/discover")
            {
                try
                {
                    _logger.LogInformation("Scanning local network for eSCL compatible devices...");

                    // Probe the network for the specific eSCL scanner protocol (_uscan._tcp.local.)
                    IReadOnlyList<IZeroconfHost> results = await ZeroconfResolver.ResolveAsync("_uscan._tcp.local.", TimeSpan.FromSeconds(2));

                    // Format the discovered devices into a clean list
                    var scanners = results.Select(host => new
                    {
                        ip = host.IPAddress,
                        name = host.DisplayName ?? "Unknown Scanner"
                    }).ToList();

                    // Serialize to JSON and send to the browser
                    string jsonScanners = JsonSerializer.Serialize(scanners);
                    byte[] buffer = Encoding.UTF8.GetBytes(jsonScanners);

                    response.ContentType = "application/json";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Network discovery failed.");
                    byte[] errorBuffer = Encoding.UTF8.GetBytes("{\"error\":\"Discovery failed\"}");
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                }
                finally
                {
                    response.Close();
                }
                return;
            }
            // Route matching for the dynamic endpoint
            if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/scan")
            {
                try
                {
                    // Extract the dynamic 'ip' target parameter from the request query string
                    string scannerIp = request.QueryString["ip"];

                    if (string.IsNullOrWhiteSpace(scannerIp))
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        byte[] errorBuffer = Encoding.UTF8.GetBytes("{\"error\":\"Missing required 'ip' query parameter.\"}");
                        response.ContentType = "application/json";
                        await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                        response.Close();
                        return;
                    }

                    _logger.LogInformation("Initializing eSCL pipeline handshakes for hardware target IP: {Ip}", scannerIp);

                  string eSclXmlTicket = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<scan:ScanSettings xmlns:scan=""http://schemas.hp.com/imaging/escl/2011/05/03"" xmlns:pwg=""http://www.pwg.org/schemas/2010/12/sm"">
    <pwg:Version>2.63</pwg:Version>
    <scan:Intent>Document</scan:Intent>
    <pwg:ScanRegions>
        <pwg:ScanRegion>
            <pwg:XOffset>0</pwg:XOffset>
            <pwg:YOffset>0</pwg:YOffset>
            <pwg:Width>2550</pwg:Width>
            <pwg:Height>3300</pwg:Height>
        </pwg:ScanRegion>
    </pwg:ScanRegions>
    <pwg:InputSource>Platen</pwg:InputSource>
    <scan:ColorMode>Grayscale8</scan:ColorMode>
    <scan:DocumentFormat>application/pdf</scan:DocumentFormat>
    <scan:XResolution>300</scan:XResolution>
    <scan:YResolution>300</scan:YResolution>
</scan:ScanSettings>";

                    var xmlContent = new StringContent(eSclXmlTicket, Encoding.UTF8, "text/xml");

                    // STEP 1: Post the XML data to reserve an active scanning job slot on the target network scanner
                    string scanJobsUrl = $"http://{scannerIp}/eSCL/ScanJobs";
                    HttpResponseMessage postResponse = await _httpClient.PostAsync(scanJobsUrl, xmlContent);

                    if (!postResponse.IsSuccessStatusCode)
                    {
                        throw new Exception($"Network scanner hardware at {scannerIp} rejected job registration. Status code: {postResponse.StatusCode}");
                    }

                    // STEP 2: Capture the unique tracking location token URL returned in the response headers
                    Uri jobTrackingLocation = postResponse.Headers.Location;
                    if (jobTrackingLocation == null)
                    {
                        throw new Exception("The scanner accepted the request but failed to generate a valid tracking Location session token.");
                    }

                    // STEP 3: Stream down the processing document image array block directly from hardware memory
                    _logger.LogInformation("Job successfully created. Pulling raw document binary stream...");
                    string documentFetchUrl = $"{jobTrackingLocation.AbsoluteUri}/NextDocument";
                    byte[] rawPdfBytes = await _httpClient.GetByteArrayAsync(documentFetchUrl);

                    // STEP 4: Convert the raw binary array into a clean Base64 string payload to pass to JavaScript
                    string base64Payload = Convert.ToBase64String(rawPdfBytes);
                    string jsonResult = "{\"pdfData\":\"data:application/pdf;base64," + base64Payload + "\"}";
                    byte[] responseBuffer = Encoding.UTF8.GetBytes(jsonResult);

                    response.ContentType = "application/json";
                    response.ContentLength64 = responseBuffer.Length;
                    await response.OutputStream.WriteAsync(responseBuffer, 0, responseBuffer.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An unhandled exception broke the transaction proxy pipeline execution stream.");

                    string safeErrorMessage = ex.Message.Replace("\"", "\\\"");
                    string jsonError = "{\"error\":\"" + safeErrorMessage + "\"}";
                    byte[] errorBuffer = Encoding.UTF8.GetBytes(jsonError);

                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.ContentType = "application/json";
                    response.ContentLength64 = errorBuffer.Length;
                    await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                }
                finally
                {
                    response.Close();
                }
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
            }
        }

        public override void Dispose()
        {
            _httpListener?.Stop();
            _httpClient?.Dispose();
            base.Dispose();
        }
    }
}