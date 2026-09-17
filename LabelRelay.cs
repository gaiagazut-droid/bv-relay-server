using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class LabelRelayEndpoints
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, List<DateTime>> UploadsByIp = new();

    public static void MapLabelRelay(this WebApplication app)
    {
        var options = app.Configuration.GetSection("LabelRelay").Get<LabelRelayOptions>() ?? new();

        app.MapGet("/imprimer/{publicKey}", (string publicKey) =>
        {
            if (!PublicKeyIsValid(options, publicKey)) return Results.NotFound();
            return Results.Content(ClientPage.Replace("PUBLIC_KEY", Uri.EscapeDataString(publicKey)), "text/html; charset=utf-8");
        });

        app.MapPost("/api/qr-print/{publicKey}/upload", async (string publicKey, HttpRequest request) =>
        {
            if (!PublicKeyIsValid(options, publicKey)) return Results.NotFound();
            if (!AllowUpload(request)) return Results.Json(new { message = "Trop de tentatives. Réessayez dans quelques minutes." }, statusCode: 429);
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "Formulaire invalide." });

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest(new { message = "Choisissez un fichier PDF." });
            if (file.Length > options.MaxUploadMb * 1024L * 1024L)
                return Results.Json(new { message = $"Le PDF dépasse la limite de {options.MaxUploadMb} Mo." }, statusCode: 413);
            if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { message = "Seuls les fichiers PDF sont acceptés." }, statusCode: 415);

            Cleanup(options);
            var id = Guid.NewGuid().ToString("N");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var directory = JobDirectory(options, id);
            Directory.CreateDirectory(directory);
            var sourcePath = Path.Combine(directory, "source.pdf");
            await using (var output = File.Create(sourcePath)) await file.CopyToAsync(output);

            var header = new byte[5];
            await using (var input = File.OpenRead(sourcePath))
            {
                if (await input.ReadAsync(header) != header.Length || Encoding.ASCII.GetString(header) != "%PDF-")
                {
                    Directory.Delete(directory, true);
                    return Results.Json(new { message = "Ce fichier n’est pas un PDF valide." }, statusCode: 415);
                }
            }

            var metadata = new LabelJob
            {
                Id = id,
                ClientTokenHash = Hash(token),
                Status = "uploaded",
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(options.RetentionMinutes)
            };
            await SaveJob(directory, metadata);
            return Results.Ok(new { id, token, expiresIn = options.RetentionMinutes * 60 });
        });

        app.MapGet("/api/qr-print/{publicKey}/jobs/{id}", async (string publicKey, string id, string token) =>
        {
            var access = await GetClientJob(options, publicKey, id, token);
            if (access.Error is not null) return access.Error;
            var job = access.Job!;
            return Results.Ok(new
            {
                status = job.Status,
                message = job.Message,
                preview = job.Status is "prepared" or "print_requested" ? $"/api/qr-print/{Uri.EscapeDataString(publicKey)}/jobs/{id}/preview?token={Uri.EscapeDataString(token)}" : null
            });
        });

        app.MapGet("/api/qr-print/{publicKey}/jobs/{id}/preview", async (string publicKey, string id, string token) =>
        {
            var access = await GetClientJob(options, publicKey, id, token);
            if (access.Error is not null) return access.Error;
            var path = Path.Combine(access.Directory!, "preview.png");
            return File.Exists(path)
                ? Results.File(path, "image/png", enableRangeProcessing: false)
                : Results.NotFound();
        });

        app.MapPost("/api/qr-print/{publicKey}/jobs/{id}/print", async (string publicKey, string id, string token) =>
        {
            await Gate.WaitAsync();
            try
            {
                var access = await GetClientJob(options, publicKey, id, token, cleanup: false);
                if (access.Error is not null) return access.Error;
                var job = access.Job!;
                if (job.Status != "prepared")
                    return Results.Json(new { message = job.Status == "print_requested" ? "Cette étiquette a déjà été envoyée." : "L’étiquette n’est pas prête." }, statusCode: 409);
                job.Status = "print_requested";
                await SaveJob(access.Directory!, job);
                return Results.Ok(new { message = "Impression demandée." });
            }
            finally { Gate.Release(); }
        });

        app.MapGet("/api/label-agent/jobs", async (HttpRequest request) =>
        {
            if (!AgentIsValid(options, request)) return Results.NotFound();
            Cleanup(options);
            var pending = new List<object>();
            if (Directory.Exists(options.DataPath))
            {
                foreach (var directory in Directory.EnumerateDirectories(options.DataPath))
                {
                    var job = await LoadJob(directory);
                    if (job is not null && job.Status is "uploaded" or "print_requested")
                        pending.Add(new { id = job.Id, status = job.Status, expiresAtUtc = job.ExpiresAtUtc });
                }
            }
            return Results.Ok(pending);
        });

        app.MapGet("/api/label-agent/jobs/{id}/source", async (string id, HttpRequest request) =>
        {
            if (!AgentIsValid(options, request)) return Results.NotFound();
            var access = await GetAgentJob(options, id);
            if (access.Error is not null) return access.Error;
            var path = Path.Combine(access.Directory!, "source.pdf");
            return File.Exists(path) ? Results.File(path, "application/pdf") : Results.NotFound();
        });

        app.MapGet("/api/label-agent/jobs/{id}/prepared", async (string id, HttpRequest request) =>
        {
            if (!AgentIsValid(options, request)) return Results.NotFound();
            await Gate.WaitAsync();
            try
            {
                var access = await GetAgentJob(options, id, cleanup: false);
                if (access.Error is not null) return access.Error;
                if (access.Job!.Status != "print_requested") return Results.Conflict();
                var path = Path.Combine(access.Directory!, "prepared.pdf");
                if (!File.Exists(path)) return Results.NotFound();
                // Claim before sending the file: a lost acknowledgement can
                // never cause a second physical print.
                access.Job.Status = "printing";
                await SaveJob(access.Directory!, access.Job);
                return Results.File(path, "application/pdf");
            }
            finally { Gate.Release(); }
        });

        app.MapPost("/api/label-agent/jobs/{id}/prepared", async (string id, HttpRequest request) =>
        {
            if (!AgentIsValid(options, request)) return Results.NotFound();
            if (!request.HasFormContentType) return Results.BadRequest();
            await Gate.WaitAsync();
            try
            {
                var access = await GetAgentJob(options, id, cleanup: false);
                if (access.Error is not null) return access.Error;
                var job = access.Job!;
                if (job.Status != "uploaded") return Results.Conflict();
                var form = await request.ReadFormAsync();
                var pdf = form.Files.GetFile("pdf");
                var preview = form.Files.GetFile("preview");
                if (pdf is null || preview is null || pdf.Length > 10 * 1024 * 1024 || preview.Length > 3 * 1024 * 1024)
                    return Results.BadRequest();
                await using (var stream = File.Create(Path.Combine(access.Directory!, "prepared.pdf"))) await pdf.CopyToAsync(stream);
                await using (var stream = File.Create(Path.Combine(access.Directory!, "preview.png"))) await preview.CopyToAsync(stream);
                job.Status = "prepared";
                await SaveJob(access.Directory!, job);
                return Results.Ok();
            }
            finally { Gate.Release(); }
        });

        app.MapPost("/api/label-agent/jobs/{id}/failed", async (string id, HttpRequest request) =>
        {
            if (!AgentIsValid(options, request)) return Results.NotFound();
            var failure = await request.ReadFromJsonAsync<AgentFailure>() ?? new();
            var access = await GetAgentJob(options, id);
            if (access.Error is not null) return access.Error;
            access.Job!.Status = "failed";
            access.Job.Message = string.IsNullOrWhiteSpace(failure.Message) ? "Le PDF n’a pas pu être préparé." : failure.Message[..Math.Min(180, failure.Message.Length)];
            await SaveJob(access.Directory!, access.Job);
            return Results.Ok();
        });

        app.MapPost("/api/label-agent/jobs/{id}/printed", async (string id, HttpRequest request) =>
        {
            if (!AgentIsValid(options, request)) return Results.NotFound();
            await Gate.WaitAsync();
            try
            {
                var access = await GetAgentJob(options, id, cleanup: false);
                if (access.Error is not null) return access.Error;
                if (access.Job!.Status != "printing") return Results.Conflict();
                access.Job.Status = "printed";
                access.Job.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(45);
                access.Job.Message = "Votre étiquette a été envoyée à l’imprimante.";
                foreach (var name in new[] { "source.pdf", "prepared.pdf", "preview.png" })
                {
                    var path = Path.Combine(access.Directory!, name);
                    if (File.Exists(path)) File.Delete(path);
                }
                await SaveJob(access.Directory!, access.Job);
                return Results.Ok();
            }
            finally { Gate.Release(); }
        });
    }

    private static bool PublicKeyIsValid(LabelRelayOptions options, string supplied) =>
        options.Enabled && SecretEquals(options.PublicKey, supplied);

    private static bool AgentIsValid(LabelRelayOptions options, HttpRequest request) =>
        options.Enabled && SecretEquals(options.AgentKey, request.Headers["X-BV-Agent-Key"].ToString());

    private static bool SecretEquals(string expected, string supplied)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }

    private static bool AllowUpload(HttpRequest request)
    {
        var ip = request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                 ?? request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTime.UtcNow;
        var events = UploadsByIp.GetOrAdd(ip, _ => new());
        lock (events)
        {
            events.RemoveAll(stamp => stamp < now.AddMinutes(-10));
            if (events.Count >= 8) return false;
            events.Add(now);
            return true;
        }
    }

    private static string JobDirectory(LabelRelayOptions options, string id) => Path.Combine(options.DataPath, SafeId(id));
    private static string SafeId(string id) => new(id.Where(char.IsAsciiLetterOrDigit).Take(64).ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<(LabelJob? Job, string? Directory, IResult? Error)> GetClientJob(LabelRelayOptions options, string publicKey, string id, string token, bool cleanup = true)
    {
        if (!PublicKeyIsValid(options, publicKey)) return (null, null, Results.NotFound());
        if (cleanup) Cleanup(options);
        var directory = JobDirectory(options, id);
        var job = await LoadJob(directory);
        if (job is null) return (null, null, Results.Json(new { message = "Cette étiquette a expiré. Recommencez l’envoi." }, statusCode: 410));
        if (!SecretEquals(job.ClientTokenHash, Hash(token))) return (null, null, Results.NotFound());
        return (job, directory, null);
    }

    private static async Task<(LabelJob? Job, string? Directory, IResult? Error)> GetAgentJob(LabelRelayOptions options, string id, bool cleanup = true)
    {
        if (cleanup) Cleanup(options);
        var directory = JobDirectory(options, id);
        var job = await LoadJob(directory);
        return job is null ? (null, null, Results.NotFound()) : (job, directory, null);
    }

    private static string MetadataPath(string directory) => Path.Combine(directory, "label-job.json");
    private static async Task SaveJob(string directory, LabelJob job) =>
        await File.WriteAllTextAsync(MetadataPath(directory), JsonSerializer.Serialize(job));

    private static async Task<LabelJob?> LoadJob(string directory)
    {
        try
        {
            var path = MetadataPath(directory);
            return File.Exists(path) ? JsonSerializer.Deserialize<LabelJob>(await File.ReadAllTextAsync(path)) : null;
        }
        catch { return null; }
    }

    private static void Cleanup(LabelRelayOptions options)
    {
        if (!Directory.Exists(options.DataPath)) return;
        foreach (var directory in Directory.EnumerateDirectories(options.DataPath))
        {
            try
            {
                var job = LoadJob(directory).GetAwaiter().GetResult();
                if (job is null || job.ExpiresAtUtc <= DateTime.UtcNow) Directory.Delete(directory, true);
            }
            catch { }
        }
    }

    private const string ClientPage = """
<!doctype html><html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex,nofollow"><title>Imprimer mon étiquette</title><style>
*{box-sizing:border-box}body{margin:0;min-height:100vh;padding:20px;background:#eef4f8;color:#172033;font-family:Segoe UI,Arial,sans-serif;display:grid;place-items:center}
main{width:min(520px,100%);background:#fff;border-radius:18px;padding:26px;box-shadow:0 16px 40px #10203026}h1{margin:0 0 8px;color:#08783f;font-size:27px}p{line-height:1.45}
input,button{width:100%;font-size:17px;margin-top:14px}input{padding:13px;border:1px solid #cbd6df;border-radius:11px;background:#f8fafc}button{border:0;border-radius:11px;padding:15px;background:#08783f;color:#fff;font-weight:750}button:disabled{opacity:.55}
#preview{display:none;width:100%;max-height:52vh;object-fit:contain;margin:18px 0 2px;border:1px solid #d7e0e7;border-radius:9px}.note{font-size:13px;color:#5c6874}#status{font-weight:700;min-height:24px}
</style></head><body><main><h1>Imprimer mon étiquette</h1><p>Choisissez le PDF de votre bordereau de transport.</p>
<input id="file" type="file" accept=".pdf,application/pdf"><button id="prepare">Préparer mon étiquette</button><p id="status"></p>
<img id="preview" alt="Aperçu de l’étiquette"><button id="print" style="display:none">Imprimer mon étiquette</button>
<p class="note">Une seule copie. Le document est supprimé automatiquement après l’impression ou au bout de quelques minutes.</p></main><script>
const key='PUBLIC_KEY',statusEl=document.querySelector('#status'),preview=document.querySelector('#preview'),printBtn=document.querySelector('#print'),prepareBtn=document.querySelector('#prepare');let job=null,timer=null;
async function json(r){const d=await r.json().catch(()=>({message:'Une erreur est survenue.'}));if(!r.ok)throw new Error(d.message||'Une erreur est survenue.');return d}
prepareBtn.onclick=async()=>{const file=document.querySelector('#file').files[0];if(!file){statusEl.textContent='Choisissez un fichier PDF.';return}prepareBtn.disabled=true;statusEl.textContent='Envoi et préparation en cours…';preview.style.display='none';printBtn.style.display='none';const form=new FormData();form.append('file',file);try{job=await json(await fetch(`/api/qr-print/${key}/upload`,{method:'POST',body:form}));poll()}catch(e){statusEl.textContent=e.message;prepareBtn.disabled=false}};
async function poll(){if(!job)return;try{const d=await json(await fetch(`/api/qr-print/${key}/jobs/${job.id}?token=${encodeURIComponent(job.token)}`,{cache:'no-store'}));if(d.status==='prepared'){statusEl.textContent='Vérifiez l’aperçu avant d’imprimer.';preview.src=d.preview+'&v='+Date.now();preview.style.display='block';printBtn.style.display='block';prepareBtn.disabled=false;return}if(d.status==='printed'){statusEl.textContent=d.message;printBtn.style.display='none';return}if(d.status==='failed'){statusEl.textContent=d.message;prepareBtn.disabled=false;return}statusEl.textContent=(d.status==='print_requested'||d.status==='printing')?'Impression en cours…':'Préparation en cours…';timer=setTimeout(poll,1500)}catch(e){statusEl.textContent=e.message;prepareBtn.disabled=false}};
printBtn.onclick=async()=>{printBtn.disabled=true;statusEl.textContent='Envoi à l’imprimante…';try{await json(await fetch(`/api/qr-print/${key}/jobs/${job.id}/print?token=${encodeURIComponent(job.token)}`,{method:'POST'}));poll()}catch(e){statusEl.textContent=e.message;printBtn.disabled=false}};
</script></body></html>
""";
}

public sealed class LabelRelayOptions
{
    public string PublicKey { get; set; } = "";
    public string AgentKey { get; set; } = "";
    public string DataPath { get; set; } = "label-data";
    public int MaxUploadMb { get; set; } = 10;
    public int RetentionMinutes { get; set; } = 5;
    public bool Enabled => PublicKey.Length >= 24 && AgentKey.Length >= 32;
}

public sealed class LabelJob
{
    public string Id { get; set; } = "";
    public string ClientTokenHash { get; set; } = "";
    public string Status { get; set; } = "uploaded";
    public string Message { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class AgentFailure
{
    public string Message { get; set; } = "";
}
