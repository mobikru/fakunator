using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Fakunator.Core.AmsApi;

// DTO для AMS Enterprise 2.x JSON-RPC API. Поля названы в camelCase — так их
// присылает сервер; десериализатор настроен на JsonNamingPolicy.CamelCase.

/// <summary>Рассылка (mailing/transactional/validation).</summary>
public class AmsMailing
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>mailing / transactional / validation.</summary>
    public string Type { get; set; } = "";
    /// <summary>idle / working / stopping.</summary>
    public string State { get; set; } = "";
    public string CreateDate { get; set; } = "";
    public string LastStartDate { get; set; } = "";
    /// <summary>Строка вида "13840/min" — сервер отдаёт как есть.</summary>
    public string ApproxSpeed { get; set; } = "";
    public bool LastStopByPostmaster { get; set; }
    public AmsProgress ProgressInfo { get; set; } = new();
    /// <summary>Присутствует только в ответе <c>getMailing(id)</c> — не в <c>getMailings()</c>.</summary>
    public AmsMailingSettings? Settings { get; set; }
}

/// <summary>Раскрытые связи рассылки, приходят только в <c>getMailing(id)</c>.</summary>
public class AmsMailingSettings
{
    public AmsIdName? SenderAccount { get; set; }
    public AmsIdName? MailList { get; set; }
    public AmsMessageRef? Message { get; set; }
    public AmsIdName? DeliveryPreset { get; set; }
}
public class AmsIdName { public int Id { get; set; } public string Name { get; set; } = ""; }
public class AmsMessageRef { public int Id { get; set; } public string Name { get; set; } = ""; public string Subject { get; set; } = ""; }

/// <summary>Плоская обёртка над <see cref="AmsMailingList"/> с готовым «Родитель &gt; Ребёнок &gt; Лист»
/// путём. Строится один раз при загрузке справочника — для ComboBox с иерархией папок AMS.</summary>
public class AmsMailingListNode
{
    public AmsMailingList Model { get; init; } = new();
    public int Id => Model.Id;
    public string ListName => Model.ListName;
    public int Size => Model.Size;
    public string FullPath { get; init; } = "";
    /// <summary>Уровень вложенности (0 = корень) — для indent'а в шаблоне.</summary>
    public int Depth { get; init; }
    public double IndentPx => Depth * 12;
}

/// <summary>Прогресс/статистика рассылки. Поля для validation отличаются от mailing
/// (good/bad/undetermined вместо sent/opened/clicks), но сервер шлёт JSON в одном
/// формате — просто присутствуют не все поля.</summary>
public class AmsProgress
{
    public int PercentDone { get; set; }
    public int Total { get; set; }
    // Для type=mailing:
    public int Sent { get; set; }
    public int NotSent { get; set; }
    public int Bad { get; set; }
    public int Refused { get; set; }
    public int Excluded { get; set; }
    public int Opened { get; set; }
    public int Clicks { get; set; }
    // Для type=validation:
    public int Good { get; set; }
    public int Undetermined { get; set; }
}

/// <summary>Учётная запись отправителя (Sender Account).</summary>
public class AmsSenderAccount
{
    public int Id { get; set; }
    public string AccountName { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string ReplyToEmail { get; set; } = "";
    public string Organization { get; set; } = "";
    public string EspDomain { get; set; } = "";
}

/// <summary>Список рассылки (папка или лист адресатов).</summary>
public class AmsMailingList
{
    public int Id { get; set; }
    /// <summary>-1 = корень, иначе id родительской папки.</summary>
    public int ParentID { get; set; }
    public string ListName { get; set; } = "";
    /// <summary>Local / External.</summary>
    public string ListType { get; set; } = "";
    public int Size { get; set; }
    public string LastMailing { get; set; } = "";
}

/// <summary>Письмо (шаблон сообщения).
/// В <c>getMessages()</c> приходят только базовые поля;
/// в <c>getMessage(id)</c> дополнительно приезжают <see cref="HtmlPart"/>
/// и <see cref="TextPartMode"/> (base64-encoded).</summary>
public class AmsMessage
{
    public int Id { get; set; }
    /// <summary>В getMessages() — как есть; в getMessage() — base64.</summary>
    public string Subject { get; set; } = "";
    public string MessageName { get; set; } = "";
    /// <summary>Html / Text / Multipart и т.д.</summary>
    public string MessageType { get; set; } = "";
    public bool OpenTracking { get; set; }
    public bool ClicksTracking { get; set; }
    /// <summary>HTML-часть письма в base64 — приходит только из getMessage(id).</summary>
    public string? HtmlPart { get; set; }
    /// <summary>ExtractFromHtml / TextOnly и т.п.</summary>
    public string? TextPartMode { get; set; }
}

/// <summary>Профиль отправки (Delivery Preset).</summary>
public class AmsDeliveryPreset
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>PersonalCopy / GroupCopy.</summary>
    public string SendingMethod { get; set; } = "";
    /// <summary>BuiltInServerOnly / RelaysOnly / Mixed.</summary>
    public string DeliveryMode { get; set; } = "";
    public bool ProxyUsed { get; set; }
    public int SendingThreads { get; set; }
}

/// <summary>Результат вызова addMailing/addMessage и т.п. — обычно объект с id.</summary>
public class AmsAddResult
{
    public int Id { get; set; }
}

/// <summary>Ошибка JSON-RPC.</summary>
public class AmsRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = "";
    public object? Data { get; set; }
}

/// <summary>Универсальный конверт JSON-RPC ответа.</summary>
internal class AmsRpcResponse<T>
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    public T? Result { get; set; }
    public AmsRpcError? Error { get; set; }
    /// <summary>Сервер возвращает id как число (мы шлём как строку) — читаем как JsonElement.</summary>
    public System.Text.Json.JsonElement Id { get; set; }
}

/// <summary>Параметры создания рассылки (addMailing).</summary>
public class AmsMailingCreate
{
    public string Name { get; set; } = "";
    /// <summary>mailing (по умолчанию), transactional, validation.</summary>
    public string Type { get; set; } = "mailing";
    public int SenderAccountId { get; set; }
    public int MessageId { get; set; }
    public int DeliveryPresetId { get; set; }
    /// <summary>Массив id списков рассылки-адресатов.</summary>
    public List<int> MailingListIds { get; set; } = new();
}
