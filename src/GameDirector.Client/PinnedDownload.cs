using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace GameDirector.Client;

/// <summary>A verified fetch of one pinned artifact. The artifact's identity is
/// its SHA-256, stated by the release process; the network only transports it.
/// Every hop must stay HTTPS (plain HTTP only on loopback test fixtures);
/// cross-host CDN redirects are safe because the pin, not the host, carries trust.
/// A mismatch refuses closed: nothing is left behind but a diagnosable error.</summary>
public static class PinnedDownload
{
    public const int MaxRedirects = 3;

    public static async Task Fetch(Uri uri, string expectedSha256, long maxBytes, string destination, CancellationToken ct = default)
    {
        if (expectedSha256 == null || !System.Text.RegularExpressions.Regex.IsMatch(expectedSha256, "^[a-f0-9]{64}$"))
            throw new ArgumentException("A pinned lowercase SHA-256 is required; refusing an unpinned download.");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Downloads require HTTPS (plain HTTP only for a loopback fixture).");

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        byte[]? payload = null;
        for (var hop = 0; ; hop++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new InvalidOperationException("Redirect without a location.");
                var next = new Uri(uri, location);
                if (hop >= MaxRedirects || (next.Scheme != "https" && !(next.Scheme == "http" && next.IsLoopback)))
                    throw new InvalidOperationException("Redirect policy refused: hops are bounded and must stay on HTTPS.");
                uri = next; continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Download failed with HTTP " + (int)response.StatusCode + ".");
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidOperationException("Download exceeds the pinned size budget.");
            payload = await response.Content.ReadAsByteArrayAsync(ct);
            if (payload.LongLength > maxBytes)
                throw new InvalidOperationException("Download exceeds the pinned size budget.");
            break;
        }
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (hash != expectedSha256)
            throw new InvalidOperationException("Downloaded bytes differ from the pinned SHA-256; the artifact was not installed.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, payload, ct);
        File.Move(temp, destination, true);
    }
}
