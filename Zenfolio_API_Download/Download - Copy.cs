using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.IO;
using System.Collections.Generic;
using System.Linq;

class Program
{
    private static HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; // Set timeout to 3 seconds
    private const string ApiBaseUrl = "https://api.zenfolio.com/api/1.8/zfapi.asmx";
    private static string authToken;
    private static readonly string DownloadFolder = @"C:\ZenfolioBackup";
    private static readonly string FailedDownloadsFile = @"C:\ZenfolioBackup\failed_downloads.txt";
    private static readonly string SkippedAlbumsFile = @"C:\ZenfolioBackup\skipped_albums.txt";

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
        Console.WriteLine("GetChallenge Status Code: " + response.StatusCode);
        Console.WriteLine("GetChallenge Response: " + responseString);

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
        Console.WriteLine("Authenticate Status Code: " + responseMsg.StatusCode);
        Console.WriteLine("Authenticate Response: " + responseString);

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
        Console.WriteLine("LoadGroupHierarchy Status Code: " + response.StatusCode);
        Console.WriteLine("LoadGroupHierarchy Response: " + responseString);

        if (response.IsSuccessStatusCode)
        {
            var xmlDoc = XDocument.Parse(responseString);
            var result = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}LoadGroupHierarchyResult").FirstOrDefault();
            if (result != null) return result;
            Console.WriteLine("No hierarchy result found");
            return null;
        }
        Console.WriteLine($"Hierarchy fetch failed: {responseString}");
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
            Console.WriteLine($"LoadPhotoSet (ID: {photoSetId}) Status Code: " + response.StatusCode);
            Console.WriteLine($"LoadPhotoSet Response: " + responseString);

            if (response.IsSuccessStatusCode)
            {
                var xmlDoc = XDocument.Parse(responseString);
                var photoSet = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}LoadPhotoSetResult").FirstOrDefault();
                var realmId = long.Parse(photoSet?.Element("{http://www.zenfolio.com/api/1.8}AccessDescriptor")?.Element("{http://www.zenfolio.com/api/1.8}RealmId")?.Value ?? "0");
                var setTitle = photoSet?.Element("{http://www.zenfolio.com/api/1.8}Title")?.Value ?? "Untitled";
                return (realmId, setTitle);
            }
            Console.WriteLine($"LoadPhotoSet failed, skipping: {responseString}");
            return (0, "Untitled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LoadPhotoSet failed for {photoSetId}: {ex.Message}");
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
            Console.WriteLine($"KeyringAddKeyPlain (RealmId: {realmId}) Status Code: " + response.StatusCode);
            Console.WriteLine($"KeyringAddKeyPlain Response: " + responseString);

            if (response.IsSuccessStatusCode)
            {
                var xmlDoc = XDocument.Parse(responseString);
                var result = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}KeyringAddKeyPlainResult").FirstOrDefault();
                return result?.Value ?? keyring;
            }
            Console.WriteLine($"KeyringAddKeyPlain failed, using existing: {responseString}");
            return keyring;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"KeyringAddKeyPlain failed for RealmId {realmId}: {ex.Message}");
            return keyring;
        }
    }

    // Load detailed photo metadata
    static async Task<(string Title, string Caption, string Copyright, string Keywords, string DateTimeOriginal)> LoadPhotoAsync(string token, long photoId, string keyring)
    {
        var soapRequest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:zf=\"http://www.zenfolio.com/api/1.8\"><soap:Body><zf:LoadPhoto><zf:photoId>{photoId}</zf:photoId><zf:level>Full</zf:level></zf:LoadPhoto></soap:Body></soap:Envelope>";
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
            Console.WriteLine($"LoadPhoto (ID: {photoId}) Status Code: " + response.StatusCode);
            Console.WriteLine($"LoadPhoto Response: " + responseString);

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
            Console.WriteLine($"LoadPhoto failed for {photoId}, returning empty metadata");
            return ("", "", "", "", "1900:01:01 00:00:00");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LoadPhoto failed for {photoId}: {ex.Message}");
            return ("", "", "", "", "1900:01:01 00:00:00");
        }
    }

    // Load photos from a PhotoSet with keyring, including basic metadata, with retry logic
    static async Task<List<(long Id, string OriginalUrl, string FileName, string Caption)>> LoadPhotoSetPhotosAsync(string token, long photoSetId, string keyring)
    {
        var photos = new List<(long Id, string OriginalUrl, string FileName, string Caption)>();
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
                    Console.WriteLine($"LoadPhotoSetPhotos (ID: {photoSetId}, Index: {startingIndex}) Status Code: " + response.StatusCode);
                    Console.WriteLine($"LoadPhotoSetPhotos Response: " + responseString);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Failed to load photos for PhotoSet {photoSetId} at index {startingIndex}, attempt {retry + 1}/{maxRetries}");
                        if (responseString.Contains("retrieving") || responseString.Contains("archived"))
                        {
                            Console.WriteLine($"PhotoSet {photoSetId} appears to be archived, skipping for now");
                            await File.AppendAllLinesAsync(SkippedAlbumsFile, new[] { $"PhotoSet:{photoSetId}" });
                            return photos; // Exit early for archived sets, but return what we have
                        }
                        else if (retry == maxRetries - 1)
                        {
                            await File.AppendAllLinesAsync(FailedDownloadsFile, new[] { $"PhotoSet:{photoSetId}:{startingIndex}" });
                            break; // Final failure, log and move to next batch
                        }
                        await Task.Delay(1000 * (retry + 1)); // Exponential backoff
                        continue;
                    }

                    var xmlDoc = XDocument.Parse(responseString);
                    var batchPhotos = xmlDoc.Descendants("{http://www.zenfolio.com/api/1.8}Photo")
                        .Select(p => (
                            Id: long.Parse(p.Element("{http://www.zenfolio.com/api/1.8}Id")?.Value ?? "0"),
                            OriginalUrl: p.Element("{http://www.zenfolio.com/api/1.8}OriginalUrl")?.Value ?? "",
                            FileName: p.Element("{http://www.zenfolio.com/api/1.8}FileName")?.Value ?? $"photo_{p.Element("{http://www.zenfolio.com/api/1.8}Id")?.Value ?? "unknown"}.jpg",
                            Caption: p.Element("{http://www.zenfolio.com/api/1.8}Caption")?.Value ?? ""
                        ))
                        .Where(p => !string.IsNullOrEmpty(p.OriginalUrl))
                        .ToList();

                    Console.WriteLine($"Found {batchPhotos.Count} photos in batch starting at {startingIndex}");
                    if (batchPhotos.Count == 0) return photos; // No more photos, exit
                    photos.AddRange(batchPhotos);
                    startingIndex += batchSize;
                    success = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LoadPhotoSetPhotos failed for PhotoSet {photoSetId} at index {startingIndex}, attempt {retry + 1}/{maxRetries}: {ex.Message}");
                    if (retry == maxRetries - 1)
                    {
                        await File.AppendAllLinesAsync(FailedDownloadsFile, new[] { $"PhotoSet:{photoSetId}:{startingIndex}" });
                        break; // Final failure, log and move on
                    }
                    // Reinitialize HttpClient on network-related exceptions
                    if (ex is HttpRequestException || ex is TaskCanceledException)
                    {
                        client.Dispose();
                        client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    }
                    await Task.Delay(1000 * (retry + 1)); // Exponential backoff
                }
            }
            if (!success) break; // Move to next PhotoSet if all retries fail
        }
        return photos;
    }

    // Download a photo with metadata, avoiding duplicates
    static async Task DownloadPhotoAsync(string token, string keyring, long photoId, string url, string filePath, string photoTitle, string caption, string copyright, string keywords, string dateTimeOriginal)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Console.WriteLine($"File already exists, skipping download: {filePath}");
                return;
            }

            Console.WriteLine($"Attempting to download from: {url}");
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = File.Create(filePath))
            {
                await stream.CopyToAsync(fileStream);
            }
            Console.WriteLine($"Successfully downloaded: {filePath}");

            string metadataText = $"Photo ID: {photoId}\n" +
                                 $"Title: {photoTitle}\n" +
                                 $"Caption: {caption}\n" +
                                 $"Copyright: {copyright}\n" +
                                 $"Keywords: {keywords}\n" +
                                 $"DateTimeOriginal: {dateTimeOriginal}\n";
            string sidecarFile = Path.ChangeExtension(filePath, ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarFile));
            await File.WriteAllTextAsync(sidecarFile, metadataText);
            Console.WriteLine($"Saved metadata to: {sidecarFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download {url}: {ex.Message}");
            await File.AppendAllLinesAsync(FailedDownloadsFile, new[] { $"{photoId}:{url}" });
        }
    }

    // Parse hierarchy and download photos, including collections
    static async Task ProcessHierarchyAsync(string token, XElement hierarchy)
    {
        try
        {
            if (hierarchy == null)
            {
                Console.WriteLine("Hierarchy is null, nothing to process");
                return;
            }

            var groupsAndPhotoSets = new List<XElement>();
            void CollectGroupsAndPhotoSets(XElement node)
            {
                foreach (var element in node.Elements("{http://www.zenfolio.com/api/1.8}Elements")?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    if (element.Name.LocalName == "PhotoSet" || element.Name.LocalName == "Group")
                        groupsAndPhotoSets.Add(element);
                    if (element.Name.LocalName == "Group")
                        CollectGroupsAndPhotoSets(element);
                }
            }
            CollectGroupsAndPhotoSets(hierarchy);
            Console.WriteLine($"Found {groupsAndPhotoSets.Count} Groups and PhotoSets in hierarchy");

            string keyring = "";
            foreach (var item in groupsAndPhotoSets)
            {
                string itemType = item.Name.LocalName;
                long itemId = long.Parse(item.Element("{http://www.zenfolio.com/api/1.8}Id")?.Value ?? "0");
                string setTitle = item.Element("{http://www.zenfolio.com/api/1.8}Title")?.Value ?? "Untitled";
                Console.WriteLine($"Processing {itemType}: {setTitle} (ID: {itemId})");

                if (itemType == "PhotoSet")
                {
                    List<(long Id, string OriginalUrl, string FileName, string Caption)> photos;
                    try
                    {
                        var (realmId, _) = await LoadPhotoSetAsync(token, itemId);
                        Console.WriteLine($"PhotoSet {itemId} RealmId: {realmId}, Title: {setTitle}");
                        if (realmId != 0)
                        {
                            keyring = await KeyringAddKeyPlainAsync(token, keyring, realmId, "jerome");
                        }

                        photos = await LoadPhotoSetPhotosAsync(token, itemId, keyring);
                        Console.WriteLine($"Fetched {photos.Count} photos from LoadPhotoSetPhotos for {setTitle}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error fetching photos for PhotoSet {itemId}: {ex.Message}");
                        photos = new List<(long Id, string OriginalUrl, string FileName, string Caption)>();
                    }

                    foreach (var (id, originalUrl, fileName, basicCaption) in photos)
                    {
                        try
                        {
                            var (photoTitle, caption, copyright, keywords, dateTimeOriginal) = await LoadPhotoAsync(token, id, keyring);
                            Console.WriteLine($"Photo {id}: Title={photoTitle}, Caption={caption}, Copyright={copyright}, Keywords={keywords}, DateTimeOriginal={dateTimeOriginal}");

                            string safeFileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
                            string safeSetTitle = string.Join("_", setTitle.Split(Path.GetInvalidFileNameChars()));
                            string filePath = Path.Combine(DownloadFolder, safeSetTitle, safeFileName);
                            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                            await DownloadPhotoAsync(token, keyring, id, originalUrl, filePath, photoTitle, caption, copyright, keywords, dateTimeOriginal);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error processing photo {id} in PhotoSet {itemId}: {ex.Message}");
                            await File.AppendAllLinesAsync(FailedDownloadsFile, new[] { $"Photo:{id}" });
                        }
                    }
                }
                else if (itemType == "Group")
                {
                    Console.WriteLine($"Processing collection (Group) {itemId}: {setTitle}");
                    var nestedPhotoSets = item.Elements("{http://www.zenfolio.com/api/1.8}Elements")?.Elements()
                        .Where(e => e.Name.LocalName == "PhotoSet")
                        .ToList() ?? new List<XElement>();
                    Console.WriteLine($"Found {nestedPhotoSets.Count} PhotoSets in collection {itemId}");
                    foreach (var nestedPhotoSet in nestedPhotoSets)
                    {
                        long nestedId = long.Parse(nestedPhotoSet.Element("{http://www.zenfolio.com/api/1.8}Id")?.Value ?? "0");
                        string nestedTitle = nestedPhotoSet.Element("{http://www.zenfolio.com/api/1.8}Title")?.Value ?? "Untitled";
                        Console.WriteLine($"Processing nested PhotoSet in collection: {nestedTitle} (ID: {nestedId})");

                        List<(long Id, string OriginalUrl, string FileName, string Caption)> nestedPhotos;
                        try
                        {
                            var (realmId, _) = await LoadPhotoSetAsync(token, nestedId);
                            Console.WriteLine($"Nested PhotoSet {nestedId} RealmId: {realmId}, Title: {nestedTitle}");
                            if (realmId != 0)
                            {
                                keyring = await KeyringAddKeyPlainAsync(token, keyring, realmId, "jerome");
                            }

                            nestedPhotos = await LoadPhotoSetPhotosAsync(token, nestedId, keyring);
                            Console.WriteLine($"Fetched {nestedPhotos.Count} photos from LoadPhotoSetPhotos for {nestedTitle}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error fetching photos for nested PhotoSet {nestedId}: {ex.Message}");
                            nestedPhotos = new List<(long Id, string OriginalUrl, string FileName, string Caption)>();
                        }

                        foreach (var (id, originalUrl, fileName, basicCaption) in nestedPhotos)
                        {
                            try
                            {
                                var (photoTitle, caption, copyright, keywords, dateTimeOriginal) = await LoadPhotoAsync(token, id, keyring);
                                Console.WriteLine($"Photo {id}: Title={photoTitle}, Caption={caption}, Copyright={copyright}, Keywords={keywords}, DateTimeOriginal={dateTimeOriginal}");

                                string safeFileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
                                string safeSetTitle = string.Join("_", nestedTitle.Split(Path.GetInvalidFileNameChars()));
                                string filePath = Path.Combine(DownloadFolder, safeSetTitle, safeFileName);
                                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                                await DownloadPhotoAsync(token, keyring, id, originalUrl, filePath, photoTitle, caption, copyright, keywords, dateTimeOriginal);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error processing photo {id} in nested PhotoSet {nestedId}: {ex.Message}");
                                await File.AppendAllLinesAsync(FailedDownloadsFile, new[] { $"Photo:{id}" });
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Critical error in ProcessHierarchyAsync: {ex.Message}");
        }
    }

    static async Task Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            Console.WriteLine($"Download folder set to: {DownloadFolder}");

            string loginName = "jeromelouiseboyer";
            authToken = await AuthenticateAsync(loginName, "Rosalie15!");
            Console.WriteLine($"Token: {authToken}");

            var hierarchy = await GetGroupHierarchyAsync(authToken, loginName);
            Console.WriteLine("Hierarchy fetched successfully!");
            Console.WriteLine("Hierarchy: " + hierarchy);

            await ProcessHierarchyAsync(authToken, hierarchy);
            Console.WriteLine("All photos processed!");

            if (File.Exists(FailedDownloadsFile))
            {
                var failedDownloads = File.ReadAllLines(FailedDownloadsFile);
                Console.WriteLine($"Failed downloads logged: {failedDownloads.Length} entries in {FailedDownloadsFile}");
                Console.WriteLine("Rerun the program later to retry failed downloads.");
            }
            if (File.Exists(SkippedAlbumsFile))
            {
                var skippedAlbums = File.ReadAllLines(SkippedAlbumsFile);
                Console.WriteLine($"Skipped albums (possibly archived): {skippedAlbums.Length} entries in {SkippedAlbumsFile}");
                Console.WriteLine("These albums may be archived and require retrieval. Check Zenfolio's web interface to restore them, or inform clients of potential delays.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Main Error: {ex.Message}");
        }
        finally
        {
            client.Dispose(); // Clean up HttpClient
        }
    }
}