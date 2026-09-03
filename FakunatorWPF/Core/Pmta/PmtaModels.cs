using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Fakunator.Core.Pmta;

// DTO для PMTA Web Monitor v5 API.
// Все endpoint'ы — POST с телом form-urlencoded `format=json`.
// Ответы: { "data": {...}, "status": "success", "message": "..." }

/// <summary>Конфигурация одной PMTA-панели. Хранится в Config.PmtaPanels.</summary>
public class PmtaPanel
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8080;
    public bool UseHttps { get; set; } = true;
    /// <summary>Игнорировать self-signed certificate. Для PMTA почти всегда true.</summary>
    public bool IgnoreCertErrors { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

// ── /status ──────────────────────────────────────────────────────────

public class PmtaStatusResponse
{
    public PmtaStatusData? Data { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

public class PmtaStatusData
{
    public PmtaMtaInfo? Mta { get; set; }
    public PmtaStatusInner? Status { get; set; }
}

public class PmtaMtaInfo
{
    public PmtaProduct? Product { get; set; }
    public PmtaOs? Os { get; set; }
    public string FullHostName { get; set; } = "";
    [JsonPropertyName("cpu")] public PmtaCpu? Cpu { get; set; }
    [JsonPropertyName("ram")] public PmtaRam? Ram { get; set; }
}

public class PmtaProduct
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Variant { get; set; } = "";
    public string BuildDate { get; set; } = "";
    public int Bits { get; set; }
}

public class PmtaOs { public string Name { get; set; } = ""; public string Version { get; set; } = ""; }
public class PmtaCpu { public string Type { get; set; } = ""; public int Count { get; set; } }
public class PmtaRam { public long Real { get; set; } }

public class PmtaStatusInner
{
    public string TimeNow { get; set; } = "";
    public string StartupTime { get; set; } = "";
    public bool ShuttingDown { get; set; }
    public PmtaTraffic? Traffic { get; set; }
    public PmtaConnections? Conn { get; set; }
    /// <summary>Реальная очередь по типам доставки. queue.smtp.rcp = что ждёт отправки.</summary>
    public PmtaQueueInfo? Queue { get; set; }
    /// <summary>Spool: физический уровень очереди, totalRcp = сколько всего в файлах.</summary>
    public PmtaSpoolInfo? Spool { get; set; }
    /// <summary>«running» / «stopping» и т.п.</summary>
    public string Status { get; set; } = "";
}

public class PmtaQueueInfo
{
    public PmtaQueueBucket? Smtp { get; set; }
    public PmtaQueueBucket? Pipe { get; set; }
    public PmtaQueueBucket? Discard { get; set; }
    public PmtaQueueBucket? File { get; set; }
    public PmtaQueueBucket? Alias { get; set; }
    public PmtaQueueBucket? BounceProcessor { get; set; }
    public PmtaQueueBucket? FeedbackLoopProcessor { get; set; }
    public PmtaQueueBucket? HttpDelivery { get; set; }
}

public class PmtaQueueBucket
{
    public long Rcp { get; set; }
    public int Dom { get; set; }
    public double Kb { get; set; }
}

public class PmtaSpoolInfo
{
    public int InitPct { get; set; }
    public int Dirs { get; set; }
    public PmtaSpoolFiles? Files { get; set; }
    public long TotalRcp { get; set; }
    public long MaxRcp { get; set; }
}

public class PmtaSpoolFiles
{
    public long InUse { get; set; }
    public long Recycled { get; set; }
    public long Total { get; set; }
}

public class PmtaTraffic
{
    public PmtaTrafficWindow? Total { get; set; }
    public PmtaTrafficWindow? LastHr { get; set; }
    public PmtaTrafficWindow? LastMin { get; set; }
    public PmtaTrafficWindow? TopPerHr { get; set; }
    public PmtaTrafficWindow? TopPerMin { get; set; }
}

public class PmtaTrafficWindow
{
    public PmtaTrafficIO? Out { get; set; }
    public PmtaTrafficIO? In { get; set; }
    public long BounceProcessed { get; set; }
    public long FeedbackLoopProcessed { get; set; }
}

public class PmtaTrafficIO
{
    public long Rcp { get; set; }
    public long Msg { get; set; }
    public double Kb { get; set; }
}

public class PmtaConnections
{
    public PmtaConnCounter? SmtpIn { get; set; }
    public PmtaConnCounter? SmtpOut { get; set; }
}

public class PmtaConnCounter
{
    public int Cur { get; set; }
    public int Max { get; set; }
    public int Top { get; set; }
}

// ── /queues ──────────────────────────────────────────────────────────

public class PmtaQueuesResponse
{
    public PmtaQueuesData? Data { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

public class PmtaQueuesData
{
    public string Title { get; set; } = "";
    public List<PmtaQueue> Queues { get; set; } = new();
}

public class PmtaQueue
{
    public string Name { get; set; } = "";
    public long Rcp { get; set; }
    public double Kb { get; set; }
    public int Conn { get; set; }
    public List<PmtaError> Errors { get; set; } = new();
}

public class PmtaError
{
    public string Time { get; set; } = "";
    public string Text { get; set; } = "";
}

// ── /domains ─────────────────────────────────────────────────────────

public class PmtaDomainsResponse
{
    public PmtaDomainsData? Data { get; set; }
    public string Status { get; set; } = "";
}

public class PmtaDomainsData
{
    public string Title { get; set; } = "";
    public List<PmtaDomain> Domains { get; set; } = new();
}

public class PmtaDomain
{
    public string Name { get; set; } = "";
    public long Rcp { get; set; }
    public double Kb { get; set; }
    public int Conn { get; set; }
    public List<PmtaError> Errors { get; set; } = new();
}

// ── /vmtas ───────────────────────────────────────────────────────────

public class PmtaVmtasResponse
{
    public PmtaVmtasData? Data { get; set; }
    public string Status { get; set; } = "";
}

public class PmtaVmtasData
{
    public string Title { get; set; } = "";
    public List<PmtaVmta> Vmtas { get; set; } = new();
}

public class PmtaVmta
{
    public string Name { get; set; } = "";
    public long Rcp { get; set; }
    public double Kb { get; set; }
    public int PctOfTotal { get; set; }
    public int Conn { get; set; }
    public int Dom { get; set; }
}

// ── /jobs ────────────────────────────────────────────────────────────

public class PmtaJobsResponse
{
    public PmtaJobsData? Data { get; set; }
    public string Status { get; set; } = "";
}

public class PmtaJobsData
{
    public string Title { get; set; } = "";
    public List<PmtaJob> Jobs { get; set; } = new();
    public int TotalJobsWithRcp { get; set; }
    public long TotalRcp { get; set; }
}

public class PmtaJob
{
    public string Name { get; set; } = "";
    public long Rcp { get; set; }
    public double Kb { get; set; }
    public int Conn { get; set; }
}
