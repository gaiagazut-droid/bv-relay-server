using System.Diagnostics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 50L * 1024 * 1024;
});

var app = builder.Build();
var relay = app.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions();
Directory.CreateDirectory(relay.DataPath);

var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff",
    ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
};

var officeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
};

app.MapGet("/", () => Results.Redirect("/s/bureau-vallee-grasse"));

app.MapGet("/s/{storeId}", (string storeId) =>
{
    var html = """
<!doctype html>
<html lang="fr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Envoyer un fichier</title>
  <style>
    body{font-family:Segoe UI,Arial,sans-serif;margin:0;min-height:100vh;background:#eef4f8;color:#172033;display:grid;place-items:center;padding:24px}
    main{width:min(520px,100%);background:white;border-radius:14px;padding:28px;box-shadow:0 18px 45px rgba(0,0,0,.18)}
    h1{margin:0 0 10px;color:#007A3D;font-size:28px}
    p{line-height:1.45}
    input,button{width:100%;font-size:18px;margin-top:14px}
    input{border:1px solid #cfd8e3;border-radius:10px;padding:12px;background:#f8fafc}
    button{border:0;border-radius:10px;padding:14px;background:#007A3D;color:white;font-weight:700}
    .note{font-size:14px;color:#526070}
    #status{font-weight:700;margin-top:18px}
  </style>
</head>
<body>
  <main>
    <h1>Envoyer un fichier</h1>
    <p>Choisissez un PDF, une image ou un document Office. Le fichier arrivera directement sur l'ordinateur du magasin.</p>
    <input id="file" type="file" accept=".pdf,.jpg,.jpeg,.png,.bmp,.tif,.tiff,.doc,.docx,.xls,.xlsx,.ppt,.pptx">
    <button id="send">Envoyer</button>
    <p class="note">Le fichier sera supprime automatiquement apres traitement.</p>
    <p id="status"></p>
  </main>
  <script>
    const status = document.getElementById('status');
    document.getElementById('send').addEventListener('click', async () => {
      const file = document.getElementById('file').files[0];
      if (!file) { status.textContent = 'Choisissez un fichier.'; return; }
      const form = new FormData();
      form.append('file', file);
      status.textContent = 'Envoi en cours...';
      const response = await fetch('/api/stores/STORE_ID/upload', { method: 'POST', body: form });
      status.textContent = response.ok ? 'Fichier recu. Vous pouvez retourner a la borne.' : await response.text();
    });
  </script>
</body>
</html>
""".Replace("STORE_ID", Uri.EscapeDataString(storeId));

    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapPost("/api/stores/{storeId}/upload", async (string storeId, HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Formulaire invalide.");
    }

    var file = request.Form.Files.GetFile("file");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest("Aucun fichier recu.");
    }

    if (file.Length > relay.MaxUploadMb * 1024L * 1024L)
    {
        return Results.BadRequest($"Fichier trop lourd. Maximum : {relay.MaxUploadMb} Mo.");
    }

    var extension = Path.GetExtension(file.FileName);
    if (!allowedExtensions.Contains(extension))
    {
        return Results.BadRequest("Type de fichier non accepte.");
    }

    var id = Guid.NewGuid().ToString("N");
    var itemDirectory = GetItemDirectory(relay, storeId, id);
    Directory.CreateDirectory(itemDirectory);

    var originalName = Path.GetFileName(file.FileName);
    var originalPath = Path.Combine(itemDirectory, "original" + extension);
    await using (var stream = File.Create(originalPath))
    {
        await file.CopyToAsync(stream);
    }

    var printablePath = originalPath;
    var printableExtension = extension;
    var status = "ready";

    if (officeExtensions.Contains(extension))
    {
        var converted = await TryConvertOfficeToPdfAsync(originalPath, itemDirectory, relay);
        if (converted is null)
        {
            status = "conversion_failed";
        }
        else
        {
            printablePath = converted;
            printableExtension = ".pdf";
        }
    }

    var metadata = new RelayFileMetadata
    {
        Id = id,
        Name = originalName,
        OriginalPath = originalPath,
        PrintablePath = printablePath,
        PrintableExtension = printableExtension,
        UploadedAtUtc = DateTime.UtcNow,
        SizeBytes = file.Length,
        Status = status
    };

    await SaveMetadataAsync(itemDirectory, metadata);
    return Results.Ok(new { id, status });
});

app.MapGet("/api/stores/{storeId}/files", async (string storeId) =>
{
    CleanupOldFiles(relay);

    var storeDirectory = GetStoreDirectory(relay, storeId);
    if (!Directory.Exists(storeDirectory))
    {
        return Results.Ok(Array.Empty<RelayFileDto>());
    }

    var files = new List<RelayFileDto>();
    foreach (var directory in Directory.EnumerateDirectories(storeDirectory))
    {
        var metadata = await LoadMetadataAsync(directory);
        if (metadata is null || metadata.Status != "ready")
        {
            continue;
        }

        files.Add(new RelayFileDto(metadata.Id, metadata.Name, metadata.UploadedAtUtc, metadata.SizeBytes, metadata.PrintableExtension));
    }

    return Results.Ok(files.OrderByDescending(file => file.UploadedAtUtc));
});

app.MapGet("/api/stores/{storeId}/files/{id}/download", async (string storeId, string id) =>
{
    var itemDirectory = GetItemDirectory(relay, storeId, id);
    var metadata = await LoadMetadataAsync(itemDirectory);
    if (metadata is null || !File.Exists(metadata.PrintablePath))
    {
        return Results.NotFound();
    }

    return Results.File(metadata.PrintablePath, "application/octet-stream", Path.GetFileName(metadata.PrintablePath));
});

app.MapDelete("/api/stores/{storeId}/files/{id}", (string storeId, string id) =>
{
    var itemDirectory = GetItemDirectory(relay, storeId, id);
    if (Directory.Exists(itemDirectory))
    {
        Directory.Delete(itemDirectory, recursive: true);
    }

    return Results.NoContent();
});

app.Run();

static string GetStoreDirectory(RelayOptions options, string storeId) =>
    Path.Combine(options.DataPath, SafeSegment(storeId));

static string GetItemDirectory(RelayOptions options, string storeId, string id) =>
    Path.Combine(GetStoreDirectory(options, storeId), SafeSegment(id));

static string SafeSegment(string value)
{
    foreach (var invalid in Path.GetInvalidFileNameChars())
    {
        value = value.Replace(invalid, '_');
    }
    return value;
}

static async Task SaveMetadataAsync(string directory, RelayFileMetadata metadata)
{
    var path = Path.Combine(directory, "metadata.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
}

static async Task<RelayFileMetadata?> LoadMetadataAsync(string directory)
{
    var path = Path.Combine(directory, "metadata.json");
    if (!File.Exists(path))
    {
        return null;
    }

    try
    {
        return JsonSerializer.Deserialize<RelayFileMetadata>(await File.ReadAllTextAsync(path));
    }
    catch
    {
        return null;
    }
}

static async Task<string?> TryConvertOfficeToPdfAsync(string sourcePath, string outputDirectory, RelayOptions options)
{
    var soffice = FindLibreOffice(options);
    if (soffice is null)
    {
        return null;
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = soffice,
        Arguments = $"--headless --convert-to pdf --outdir \"{outputDirectory}\" \"{sourcePath}\"",
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        return null;
    }

    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        return null;
    }

    var convertedPath = Path.ChangeExtension(sourcePath, ".pdf");
    return File.Exists(convertedPath) ? convertedPath : Directory.EnumerateFiles(outputDirectory, "*.pdf").FirstOrDefault();
}

static string? FindLibreOffice(RelayOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.LibreOfficePath) && File.Exists(options.LibreOfficePath))
    {
        return options.LibreOfficePath;
    }

    var candidates = new[]
    {
        "/usr/bin/libreoffice",
        "/usr/local/bin/libreoffice",
        "C:\\Program Files\\LibreOffice\\program\\soffice.exe"
    };

    return candidates.FirstOrDefault(File.Exists);
}

static void CleanupOldFiles(RelayOptions options)
{
    if (!Directory.Exists(options.DataPath))
    {
        return;
    }

    var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, options.RetentionHours));
    foreach (var metadataPath in Directory.EnumerateFiles(options.DataPath, "metadata.json", SearchOption.AllDirectories))
    {
        var directory = Path.GetDirectoryName(metadataPath);
        if (directory is null)
        {
            continue;
        }

        var metadata = LoadMetadataAsync(directory).GetAwaiter().GetResult();
        if (metadata is not null && metadata.UploadedAtUtc < cutoff)
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class RelayOptions
{
    public string DataPath { get; set; } = "data";
    public int MaxUploadMb { get; set; } = 40;
    public int RetentionHours { get; set; } = 4;
    public string LibreOfficePath { get; set; } = "";
}

public sealed class RelayFileMetadata
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string PrintablePath { get; set; } = "";
    public string PrintableExtension { get; set; } = "";
    public DateTime UploadedAtUtc { get; set; }
    public long SizeBytes { get; set; }
    public string Status { get; set; } = "ready";
}

public sealed record RelayFileDto(string Id, string Name, DateTime UploadedAtUtc, long SizeBytes, string PrintableExtension);
