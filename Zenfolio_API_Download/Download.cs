using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web; // For HttpUtility.HtmlDecode
//ZenfolioBackup.exe --download-folder "D:\Photos\Backup" --failed-downloads-file "D:\Photos\failed.txt" --skipped-albums-file "D:\Photos\skipped.txt" --login-name "myusername" --password "mypassword123"
class Program
{
    private static HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private const string ApiBaseUrl = "https://api.zenfolio.com/api/1.8/zfapi.asmx";
    private static string authToken;
    private static string DownloadFolder;
    private static string FailedDownloadsFile;
    private static string SkippedAlbumsFile;

    // Debug logging helper
    static void Log(string message)
    {
#if DEBUG
        Console.WriteLine(message);
#endif
    }

    // Helper methods from ExifRenamer
    static string RemoveHtmlTags(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return Regex.Replace(input, @"</?strong>", "");
    }

    static string DecodeHtmlEntities(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return HttpUtility.HtmlDecode(input);
    }

    // Get challenge and salt
    static async Task<(byte[] Challenge, byte[] PasswordSalt)> GetChallengeAsync(string loginName)
    {
        var soapRequest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:zf=\"http://www.zenfolio.com/api/1.8\"><soap:Body><zf:GetChallenge><zf:loginName>{loginName}</zf:loginName></zf:GetChallenge></soap:Body></soap:Envelope>";
        var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "http://www.zenfolio.com/api/1.8/GetChallenge");

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/xml");

        var response = await client.PostAsync(ApiBaseUrl, content);
        var responseString = await response.Content.ReadAsStringAsync();
        Log("GetChallenge Status Code: " + response.StatusCode);
        Log("GetChallenge Response: " + responseString);

        if (response.IsSuccessStatusCode)
        {
            var xmlDoc = XDocument.Parse(responseString);
            var challengeElement = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}Challenge").FirstOrDefault();
            var saltElement = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}PasswordSalt").FirstOrDefault();
            if (challengeElement != null && saltElement != null)
            {
                return (Convert.FromBase64String(challengeElement.Value), Convert.FromBase64String(saltElement.Value));
            }
            throw new Exception("Challenge or salt missing in response");
        }
        throw new HttpRequestException($"GetChallenge failed: {responseString}");
    }

    // Compute SHA-256 hash
    static byte[] ComputeSha256Hash(byte[] data1, byte[] data2)
    {
        using (var sha256 = SHA256.Create())
        {
            byte[] combined = new byte[data1.Length + data2.Length];
            Array.Copy(data1, 0, combined, 0, data1.Length);
            Array.Copy(data2, 0, combined, data1.Length, data2.Length);
            return sha256.ComputeHash(combined);
        }
    }

    // Authenticate using challenge-response
    static async Task<string> AuthenticateAsync(string loginName, string password)
    {
        var (challenge, salt) = await GetChallengeAsync(loginName);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[] passwordHash = ComputeSha256Hash(salt, passwordBytes);
        byte[] response = ComputeSha256Hash(challenge, passwordHash);

        var soapRequest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:zf=\"http://www.zenfolio.com/api/1.8\"><soap:Body><zf:Authenticate><zf:challenge>{Convert.ToBase64String(challenge)}</zf:challenge><zf:proof>{Convert.ToBase64String(response)}</zf:proof></zf:Authenticate></soap:Body></soap:Envelope>";
        var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "http://www.zenfolio.com/api/1.8/Authenticate");

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/xml");

        var responseMsg = await client.PostAsync(ApiBaseUrl, content);
        var responseString = await responseMsg.Content.ReadAsStringAsync();
        Log("Authenticate Status Code: " + responseMsg.StatusCode);
        Log("Authenticate Response: " + responseString);

        if (responseMsg.IsSuccessStatusCode)
        {
            var xmlDoc = XDocument.Parse(responseString);
            var tokenElement = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}AuthenticateResult").FirstOrDefault();
            if (tokenElement != null) return tokenElement.Value;
            throw new Exception("No token found");
        }
        throw new HttpRequestException($"Authenticate failed: {responseString}");
    }

    // Fetch group hierarchy
    static async Task<XElement> GetGroupHierarchyAsync(string token, string loginName)
    {
        var soapRequest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:zf=\"http://www.zenfolio.com/api/1.8\"><soap:Body><zf:LoadGroupHierarchy><zf:loginName>{loginName}</zf:loginName></zf:LoadGroupHierarchy></soap:Body></soap:Envelope>";
        var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "http://www.zenfolio.com/api/1.8/LoadGroupHierarchy");

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/xml");
        client.DefaultRequestHeaders.Add("X-Zenfolio-Token", token);

        var response = await client.PostAsync(ApiBaseUrl, content);
        var responseString = await response.Content.ReadAsStringAsync();
        Log("LoadGroupHierarchy Status Code: " + response.StatusCode);
        Log("LoadGroupHierarchy Response: " + responseString);

        if (response.IsSuccessStatusCode)
        {
            var xmlDoc = XDocument.Parse(responseString);
            var result = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}LoadGroupHierarchyResult").FirstOrDefault();
            if (result != null) return result;
            Log("No hierarchy result found");
            return null;
        }
        Log($"Hierarchy fetch failed: {responseString}");
        return null;
    }

    // Load PhotoSet details to get RealmId
    static async Task<(long RealmId, string Title)> LoadPhotoSetAsync(string token, long photoSetId)
    {
        var soapRequest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:zf=\"http://www.zenfolio.com/api/1.8\"><soap:Body><zf:LoadPhotoSet><zf:photoSetId>{photoSetId}</zf:photoSetId><zf:level>Full</zf:level><zf:includePhotos>false</zf:includePhotos></zf:LoadPhotoSet></soap:Body></soap:Envelope>";
        var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "http://www.zenfolio.com/api/1.8/LoadPhotoSet");

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/xml");
        client.DefaultRequestHeaders.Add("X-Zenfolio-Token", token);

        try
        {
            var response = await client.PostAsync(ApiBaseUrl, content);
            var responseString = await response.Content.ReadAsStringAsync();
            Log($"LoadPhotoSet (ID: {photoSetId}) Status Code: " + response.StatusCode);
            Log($"LoadPhotoSet Response: " + responseString);

            if (response.IsSuccessStatusCode)
            {
                var xmlDoc = XDocument.Parse(responseString);
                var photoSet = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}LoadPhotoSetResult").FirstOrDefault();
                var realmId = long.Parse(photoSet?.Element("{http://www.zenfolio.com/api/1.8}AccessDescriptor")?.Element("{http://www.zenfolio.com/api/1.8}RealmId")?.Value ?? "0");
                var setTitle = photoSet?.Element("{http://www.zenfolio.com/api/1.8}Title")?.Value ?? "Untitled";
                return (realmId, setTitle);
            }
            Log($"LoadPhotoSet failed, skipping: {responseString}");
            return (0, "Untitled");
        }
        catch (Exception ex)
        {
            Log($"LoadPhotoSet failed for {photoSetId}: {ex.Message}");
            return (0, "Untitled");
        }
    }

    // Add password to keyring
    static async Task<string> KeyringAddKeyPlainAsync(string token, string keyring, long realmId, string password)
    {
        var soapRequest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:zf=\"http://www.zenfolio.com/api/1.8\"><soap:Body><zf:KeyringAddKeyPlain><zf:keyring>{keyring}</zf:keyring><zf:realmId>{realmId}</zf:realmId><zf:password>{password}</zf:password></zf:KeyringAddKeyPlain></soap:Body></soap:Envelope>";
        var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "http://www.zenfolio.com/api/1.8/KeyringAddKeyPlain");

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/xml");
        client.DefaultRequestHeaders.Add("X-Zenfolio-Token", token);

        try
        {
            var response = await client.PostAsync(ApiBaseUrl, content);
            var responseString = await response.Content.ReadAsStringAsync();
            Log($"KeyringAddKeyPlain (RealmId: {realmId}) Status Code: " + response.StatusCode);
            Log($"KeyringAddKeyPlain Response: " + responseString);

            if (response.IsSuccessStatusCode)
            {
                var xmlDoc = XDocument.Parse(responseString);
                var result = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}KeyringAddKeyPlainResult").FirstOrDefault();
                return result?.Value ?? keyring;
            }
            Log($"KeyringAddKeyPlain failed, using existing: {responseString}");
            return keyring;
        }
        catch (Exception ex)
        {
            Log($"KeyringAddKeyPlain failed for RealmId {realmId}: {ex.Message}");
            return keyring;
        }
    }

    // Load detailed photo/video metadata
    static async Task<(string Title, string Caption, string Copyright, string Keywords, string DateTimeOriginal)> LoadPhotoAsync(string token, long mediaId, string keyring)
    {
        var soapRequest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:zf=\"http://www.zenfolio.com/api/1.8\"><soap:Body><zf:LoadPhoto><zf:photoId>{mediaId}</zf:photoId><zf:level>Full</zf:level></zf:LoadPhoto></soap:Body></soap:Envelope>";
        var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "http://www.zenfolio.com/api/1.8/LoadPhoto");

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/xml");
        client.DefaultRequestHeaders.Add("X-Zenfolio-Token", token);
        if (!string.IsNullOrEmpty(keyring))
            client.DefaultRequestHeaders.Add("X-Zenfolio-Keyring", keyring);

        try
        {
            var response = await client.PostAsync(ApiBaseUrl, content);
            var responseString = await response.Content.ReadAsStringAsync();
            Log($"LoadPhoto (ID: {mediaId}) Status Code: " + response.StatusCode);
            Log($"LoadPhoto Response: " + responseString);

            if (response.IsSuccessStatusCode)
            {
                var xmlDoc = XDocument.Parse(responseString);
                var photo = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}LoadPhotoResult").FirstOrDefault();
                var photoTitle = photo?.Element("{http://www.zenfolio.com/api/1.8}Title")?.Value ?? "";
                var caption = photo?.Element("{http://www.zenfolio.com/api/1.8}Caption")?.Value ?? "";
                var copyright = photo?.Element("{http://www.zenfolio.com/api/1.8}Copyright")?.Value ?? "";
                var keywordsElement = photo?.Element("{http://www.zenfolio.com/api/1.8}Keywords");
                var keywords = keywordsElement != null ? string.Join(", ", keywordsElement.Elements("{http://www.zenfolio.com/api/1.8}string").Select(e => e.Value)) : "";
                var exifTags = photo?.Elements("{http://www.zenfolio.com/api/1.8}ExifTags")
                    .FirstOrDefault(e => e.Element("{http://www.zenfolio.com/api/1.8}Id")?.Value == "36867");
                var dateTimeOriginal = exifTags?.Element("{http://www.zenfolio.com/api/1.8}Value")?.Value ?? "1900:01:01 00:00:00";
                if (DateTime.TryParse(dateTimeOriginal, out DateTime date))
                {
                    dateTimeOriginal = date.ToString("yyyy:MM:dd HH:mm:ss");
                }
                return (photoTitle, caption, copyright, keywords, dateTimeOriginal);
            }
            Log($"LoadPhoto failed for {mediaId}, returning empty metadata");
            return ("", "", "", "", "1900:01:01 00:00:00");
        }
        catch (Exception ex)
        {
            Log($"LoadPhoto failed for {mediaId}: {ex.Message}");
            return ("", "", "", "", "1900:01:01 00:00:00");
        }
    }

    // Load photos/videos from a PhotoSet with keyring, including basic metadata, with retry logic
    static async Task<List<(long Id, string OriginalUrl, string FileName, string Caption, string MimeType)>> LoadPhotoSetPhotosAsync(string token, long photoSetId, string keyring)
    {
        var mediaItems = new List<(long Id, string OriginalUrl, string FileName, string Caption, string MimeType)>();
        int startingIndex = 0;
        const int batchSize = 10;
        const int maxRetries = 3;

        while (true)
        {
            var soapRequest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:zf=\"http://www.zenfolio.com/api/1.8\"><soap:Body><zf:LoadPhotoSetPhotos><zf:photoSetId>{photoSetId}</zf:photoSetId><zf:startingIndex>{startingIndex}</zf:startingIndex><zf:numberOfPhotos>{batchSize}</zf:numberOfPhotos></zf:LoadPhotoSetPhotos></soap:Body></soap:Envelope>";
            var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", "http://www.zenfolio.com/api/1.8/LoadPhotoSetPhotos");

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/xml");
            client.DefaultRequestHeaders.Add("X-Zenfolio-Token", token);
            if (!string.IsNullOrEmpty(keyring))
                client.DefaultRequestHeaders.Add("X-Zenfolio-Keyring", keyring);

            bool success = false;
            for (int retry = 0; retry < maxRetries && !success; retry++)
            {
                try
                {
                    var response = await client.PostAsync(ApiBaseUrl, content);
                    var responseString = await response.Content.ReadAsStringAsync();
                    Log($"LoadPhotoSetPhotos (ID: {photoSetId}, Index: {startingIndex}) Status Code: " + response.StatusCode);
                    Log($"LoadPhotoSetPhotos Response: " + responseString);

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"Failed to load media for PhotoSet {photoSetId} at index {startingIndex}, attempt {retry + 1}/{maxRetries}");
                        if (responseString.Contains("retrieving") || responseString.Contains("archived"))
                        {
                            Log($"PhotoSet {photoSetId} appears to be archived, skipping for now");
                            await File.AppendAllLinesAsync(SkippedAlbumsFile, new[] { $"PhotoSet:{photoSetId}" });
                            return mediaItems;
                        }
                        else if (retry == maxRetries - 1)
                        {
                            await File.AppendAllLinesAsync(FailedDownloadsFile, new[] { $"PhotoSet:{photoSetId}:{startingIndex}" });
                            break;
                        }
                        await Task.Delay(1000 * (retry + 1));
                        continue;
                    }

                    var xmlDoc = XDocument.Parse(responseString);
                    var batchMedia = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}Photo")
                        .Select(p => (
                            Id: long.Parse(p.Element("{http://www.zenfolio.com/api/1.8}Id")?.Value ?? "0"),
                            OriginalUrl: p.Element("{http://www.zenfolio.com/api/1.8}OriginalUrl")?.Value ?? "",
                            FileName: p.Element("{http://www.zenfolio.com/api/1.8}FileName")?.Value ?? $"media_{p.Element("{http://www.zenfolio.com/api/1.8}Id")?.Value ?? "unknown"}",
                            Caption: p.Element("{http://www.zenfolio.com/api/1.8}Caption")?.Value ?? "",
                            MimeType: p.Element("{http://www.zenfolio.com/api/1.8}MimeType")?.Value ?? InferMimeTypeFromUrl(p.Element("{http://www.zenfolio.com/api/1.8}OriginalUrl")?.Value)
                        ))
                        .Where(p => !string.IsNullOrEmpty(p.OriginalUrl))
                        .ToList();

                    Log($"Found {batchMedia.Count} media items in batch starting at {startingIndex}");
                    if (batchMedia.Count == 0) return mediaItems;
                    mediaItems.AddRange(batchMedia);
                    startingIndex += batchSize;
                    success = true;
                }
                catch (Exception ex)
                {
                    Log($"LoadPhotoSetPhotos failed for PhotoSet {photoSetId} at index {startingIndex}, attempt {retry + 1}/{maxRetries}: {ex.Message}");
                    if (retry == maxRetries - 1)
                    {
                        await File.AppendAllLinesAsync(FailedDownloadsFile, new[] { $"PhotoSet:{photoSetId}:{startingIndex}" });
                        break;
                    }
                    if (ex is HttpRequestException || ex is TaskCanceledException)
                    {
                        client.Dispose();
                        client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    }
                    await Task.Delay(1000 * (retry + 1));
                }
            }
            if (!success) break;
        }
        return mediaItems;
    }

    // Helper method to infer MIME type from URL
    static string InferMimeTypeFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "application/octet-stream";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        return "application/octet-stream";
    }

    // Download a photo or video and create XMP sidecar for images only, with gallery name logging
    static async Task DownloadPhotoAsync(string token, string keyring, long mediaId, string url, string filePathBase, string mediaTitle, string caption, string copyright, string keywords, string dateTimeOriginal, string mimeType, string galleryName)
    {
        try
        {
            string extension = mimeType switch
            {
                "video/mp4" => ".mp4",
                "image/jpeg" => ".jpg",
                _ => Path.GetExtension(url)?.ToLower() ?? ".dat"
            };
            string filePath = Path.ChangeExtension(filePathBase, extension);

            if (File.Exists(filePath))
            {
                Log($"File already exists, skipping download: {filePath}");
                return;
            }

            Log($"Attempting to download from: {url}");
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = File.Create(filePath))
            {
                await stream.CopyToAsync(fileStream);
            }
            Log($"Successfully downloaded: {filePath}");

            if (mimeType.StartsWith("image/"))
            {
                string cleanTitle = mediaTitle != null ? DecodeHtmlEntities(RemoveHtmlTags(mediaTitle)) : null;
                string cleanCaption = caption != null ? DecodeHtmlEntities(RemoveHtmlTags(caption)) : null;

                string sidecarXmpFile = Path.ChangeExtension(filePath, ".xmp");
                string currentDate = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
                var xmpBuilder = new StringBuilder();
                xmpBuilder.AppendLine(@"<?xpacket begin="""" id=""W5M0MpCehiHzreSzNTczkc9d""?>");
                xmpBuilder.AppendLine(@"<x:xmpmeta xmlns:x=""adobe:ns:meta/"" x:xmptk=""ZenfolioBackup"">");
                xmpBuilder.AppendLine(@"  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">");
                xmpBuilder.AppendLine(@"    <rdf:Description rdf:about="""" xmlns:dc=""http://purl.org/dc/elements/1.1/"" xmlns:xmp=""http://ns.adobe.com/xap/1.0/"">");

                if (!string.IsNullOrEmpty(cleanTitle))
                {
                    xmpBuilder.AppendLine(@"      <dc:title>");
                    xmpBuilder.AppendLine(@"        <rdf:Alt>");
                    xmpBuilder.AppendLine($@"          <rdf:li xml:lang=""x-default"">{cleanTitle}</rdf:li>");
                    xmpBuilder.AppendLine(@"        </rdf:Alt>");
                    xmpBuilder.AppendLine(@"      </dc:title>");
                }

                if (!string.IsNullOrEmpty(cleanCaption))
                {
                    xmpBuilder.AppendLine(@"      <dc:description>");
                    xmpBuilder.AppendLine(@"        <rdf:Alt>");
                    xmpBuilder.AppendLine($@"          <rdf:li xml:lang=""x-default"">{cleanCaption}</rdf:li>");
                    xmpBuilder.AppendLine(@"        </rdf:Alt>");
                    xmpBuilder.AppendLine(@"      </dc:description>");
                }

                if (!string.IsNullOrEmpty(copyright))
                {
                    xmpBuilder.AppendLine($@"      <dc:rights>{copyright}</dc:rights>");
                }

                if (!string.IsNullOrEmpty(keywords))
                {
                    xmpBuilder.AppendLine(@"      <dc:subject>");
                    xmpBuilder.AppendLine(@"        <rdf:Bag>");
                    foreach (var keyword in keywords.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        xmpBuilder.AppendLine($@"          <rdf:li>{keyword}</rdf:li>");
                    }
                    xmpBuilder.AppendLine(@"        </rdf:Bag>");
                    xmpBuilder.AppendLine(@"      </dc:subject>");
                }

                xmpBuilder.AppendLine(@"      <xmp:CreatorTool>ZenfolioBackup</xmp:CreatorTool>");
                xmpBuilder.AppendLine($@"      <xmp:MetadataDate>{currentDate}</xmp:MetadataDate>");
                xmpBuilder.AppendLine($@"      <xmp:CreateDate>{dateTimeOriginal}</xmp:CreateDate>");
                xmpBuilder.AppendLine(@"    </rdf:Description>");
                xmpBuilder.AppendLine(@"  </rdf:RDF>");
                xmpBuilder.AppendLine(@"</x:xmpmeta>");
                xmpBuilder.AppendLine(@"<?xpacket end=""w""?>");

                string xmpString = xmpBuilder.ToString();
                Directory.CreateDirectory(Path.GetDirectoryName(sidecarXmpFile));
                if (File.Exists(sidecarXmpFile)) File.Delete(sidecarXmpFile);
                await File.WriteAllTextAsync(sidecarXmpFile, xmpString, new UTF8Encoding(false));
                Log($"Saved XMP metadata to: {sidecarXmpFile}");
            }
            else
            {
                Log($"Skipping XMP generation for video: {filePath}");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to download {url} or create XMP: {ex.Message}");
            await File.AppendAllLinesAsync(FailedDownloadsFile, new[] { galleryName, $"{mediaId}:{url}" });
        }
    }

    // Helper method to build the full folder path from the hierarchy
    static string BuildFolderPath(XElement node, XElement hierarchy, string rootName)
    {
        var pathParts = new List<string> { rootName };
        XElement current = node;

        while (current != null && current != hierarchy)
        {
            string title = current.Element("{http://www.zenfolio.com/api/1.8}Title")?.Value ?? "Untitled";
            pathParts.Insert(1, NormalizeFolderName(title));
            current = current.Parent?.Parent;
        }

        string fullPath = Path.Combine(pathParts.ToArray());
        if (fullPath.Length > 200)
        {
            Log($"Warning: Path length {fullPath.Length} may approach Windows limit: {fullPath}");
        }
        return fullPath;
    }

    // Normalize folder names for Windows compatibility
    static string NormalizeFolderName(string name)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string normalized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return normalized.Length > 50 ? normalized.Substring(0, 50) : normalized;
    }

    // Parse hierarchy and download photos/videos, excluding collections
    static async Task ProcessHierarchyAsync(string token, XElement hierarchy)
    {
        try
        {
            if (hierarchy == null)
            {
                Log("Hierarchy is null, nothing to process");
                return;
            }

            string rootName = hierarchy.Element("{http://www.zenfolio.com/api/1.8}Title")?.Value ?? "Photographs From Louise - Jerome Boyer";
            Log($"Root folder: {rootName}");

            var photoSets = new List<XElement>();
            void CollectPhotoSets(XElement node)
            {
                foreach (var element in node.Elements("{http://www.zenfolio.com/api/1.8}Elements")?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    if (element.Name.LocalName == "PhotoSet")
                        photoSets.Add(element);
                    if (element.Name.LocalName == "Group")
                        CollectPhotoSets(element);
                }
            }
            CollectPhotoSets(hierarchy);
            Log($"Found {photoSets.Count} PhotoSets in hierarchy (excluding collections)");

            string keyring = "";
            foreach (var item in photoSets)
            {
                long itemId = long.Parse(item.Element("{http://www.zenfolio.com/api/1.8}Id")?.Value ?? "0");
                string setTitle = item.Element("{http://www.zenfolio.com/api/1.8}Title")?.Value ?? "Untitled";
                Log($"Processing PhotoSet: {setTitle} (ID: {itemId})");

                List<(long Id, string OriginalUrl, string FileName, string Caption, string MimeType)> mediaItems;
                try
                {
                    var (realmId, _) = await LoadPhotoSetAsync(token, itemId);
                    Log($"PhotoSet {itemId} RealmId: {realmId}, Title: {setTitle}");
                    if (realmId != 0)
                    {
                        keyring = await KeyringAddKeyPlainAsync(token, keyring, realmId, "jerome");
                    }

                    mediaItems = await LoadPhotoSetPhotosAsync(token, itemId, keyring);
                    Log($"Fetched {mediaItems.Count} media items from LoadPhotoSetPhotos for {setTitle}");
                }
                catch (Exception ex)
                {
                    Log($"Error fetching media for PhotoSet {itemId}: {ex.Message}");
                    mediaItems = new List<(long Id, string OriginalUrl, string FileName, string Caption, string MimeType)>();
                }

                string fullFolderPath = BuildFolderPath(item, hierarchy, rootName);
                Log($"Full folder path for PhotoSet {itemId}: {fullFolderPath}");

                foreach (var (id, originalUrl, fileName, basicCaption, mimeType) in mediaItems)
                {
                    try
                    {
                        (string mediaTitle, string caption, string copyright, string keywords, string dateTimeOriginal) =
                            await LoadPhotoAsync(token, id, keyring);
                        Log($"Media {id}: Title={mediaTitle}, Caption={caption}, Copyright={copyright}, Keywords={keywords}, DateTimeOriginal={dateTimeOriginal}, MimeType={mimeType}");

                        string safeFileName = NormalizeFolderName(fileName);
                        string filePath = Path.Combine(DownloadFolder, fullFolderPath, safeFileName);
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                        await DownloadPhotoAsync(token, keyring, id, originalUrl, filePath, mediaTitle, caption, copyright, keywords, dateTimeOriginal, mimeType, setTitle);
                    }
                    catch (Exception ex)
                    {
                        Log($"Error processing media {id} in PhotoSet {itemId}: {ex.Message}");
                        await File.AppendAllLinesAsync(FailedDownloadsFile, new[] { setTitle, $"{id}:{originalUrl}" });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Critical error in ProcessHierarchyAsync: {ex.Message}");
        }
    }

    static async Task Main(string[] args)
    {
        try
        {
            // Default values
            DownloadFolder = @"C:\ZenfolioBackup";
            FailedDownloadsFile = Path.Combine(DownloadFolder, "failed_downloads.txt");
            SkippedAlbumsFile = Path.Combine(DownloadFolder, "skipped_albums.txt");
            string loginName = "";
            string password = "";

            // Parse command-line arguments
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--download-folder":
                        if (i + 1 < args.Length) DownloadFolder = args[++i];
                        break;
                    case "--failed-downloads-file":
                        if (i + 1 < args.Length) FailedDownloadsFile = args[++i];
                        break;
                    case "--skipped-albums-file":
                        if (i + 1 < args.Length) SkippedAlbumsFile = args[++i];
                        break;
                    case "--login-name":
                        if (i + 1 < args.Length) loginName = args[++i];
                        break;
                    case "--password":
                        if (i + 1 < args.Length) password = args[++i];
                        break;
                    default:
                        Log($"Unknown argument: {args[i]}");
                        break;
                }
            }

            // Validate and set defaults if necessary
            if (string.IsNullOrEmpty(DownloadFolder))
            {
                throw new ArgumentException("Download folder must be specified.");
            }
            if (string.IsNullOrEmpty(FailedDownloadsFile))
            {
                FailedDownloadsFile = Path.Combine(DownloadFolder, "failed_downloads.txt");
            }
            if (string.IsNullOrEmpty(SkippedAlbumsFile))
            {
                SkippedAlbumsFile = Path.Combine(DownloadFolder, "skipped_albums.txt");
            }
            if (string.IsNullOrEmpty(loginName) || string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Login name and password must be specified.");
            }

            Directory.CreateDirectory(DownloadFolder);
            Log($"Download folder set to: {DownloadFolder}");

            authToken = await AuthenticateAsync(loginName, password);
            Log($"Token: {authToken}");

            var hierarchy = await GetGroupHierarchyAsync(authToken, loginName);
            Log("Hierarchy fetched successfully!");
            Log("Hierarchy: " + hierarchy);

            await ProcessHierarchyAsync(authToken, hierarchy);
            Log("All media processed!");

            if (File.Exists(FailedDownloadsFile))
            {
                var failedDownloads = await File.ReadAllLinesAsync(FailedDownloadsFile);
                Log($"Failed downloads logged: {failedDownloads.Length / 2} entries in {FailedDownloadsFile}");
                Log("Rerun the program later to retry failed downloads.");
            }
            if (File.Exists(SkippedAlbumsFile))
            {
                var skippedAlbums = File.ReadAllLines(SkippedAlbumsFile);
                Log($"Skipped albums (possibly archived): {skippedAlbums.Length} entries in {SkippedAlbumsFile}");
                Log("These albums may be archived and require retrieval. Check Zenfolio's web interface to restore them, or inform clients of potential delays.");
            }
        }
        catch (Exception ex)
        {
            Log($"Main Error: {ex.Message}");
            Console.WriteLine($"Error: {ex.Message}"); // Always show errors, even in Release mode
        }
        finally
        {
            client.Dispose();
        }
    }
}