using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AppKeeper;

internal sealed record AgendaOperation(string Id, string DeviceId, long Clock, string Kind, string TaskId, JsonElement Payload, string CreatedAt)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 100 || string.IsNullOrWhiteSpace(DeviceId) || DeviceId.Length > 100 ||
            string.IsNullOrWhiteSpace(TaskId) || TaskId.Length > 100 || Clock is < 1 or > 9_007_199_254_740_000 ||
            !new[] { "create", "edit", "start", "complete", "feedback", "reopen", "delete" }.Contains(Kind) ||
            !DateTimeOffset.TryParse(CreatedAt, out _) || Payload.ValueKind != JsonValueKind.Object || Payload.GetRawText().Length > 65536)
            throw new InvalidDataException("日程同步操作无效。");
        foreach (var name in new[] { "title", "category", "note", "inputUnit" })
            if (Payload.TryGetProperty(name, out var text) && text.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("日程文字格式无效。");
        foreach (var name in new[] { "plannedStart", "deadline", "actualStartedAt", "completedAt", "startedAt" })
            if (Payload.TryGetProperty(name, out var date) && date.ValueKind != JsonValueKind.Null &&
                (date.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(date.GetString(), out _)))
                throw new InvalidDataException("日程日期格式无效。");
        foreach (var name in new[] { "startHasTime", "deadlineHasTime" })
            if (Payload.TryGetProperty(name, out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("日程时间选项无效。");
        foreach (var name in new[] { "effortSeconds", "inputValue", "hoursPerDay", "rating" })
            if (Payload.TryGetProperty(name, out var number) && number.ValueKind != JsonValueKind.Null &&
                (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var value) || value < 0 ||
                    name == "rating" && value is < 1 or > 5))
                throw new InvalidDataException("日程反馈格式无效。");
        if (Kind == "create" && (!Payload.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(title.GetString()))) throw new InvalidDataException("日程标题不能为空。");
        var requiredDate = Kind == "start" ? "actualStartedAt" : Kind == "complete" ? "completedAt" : null;
        if (requiredDate != null && (!Payload.TryGetProperty(requiredDate, out var required) || required.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("日程状态时间缺失。");
        if (Kind == "edit" && new[] { "title", "category", "startedAt" }.Any(name =>
            Payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null))
            throw new InvalidDataException("日程修改包含无效空值。");
        if (Kind == "feedback" && Payload.TryGetProperty("effortSeconds", out var effort) && effort.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException("反馈投入格式无效。");
    }
    internal bool SameAs(AgendaOperation other) => Id == other.Id && DeviceId == other.DeviceId && Clock == other.Clock &&
        Kind == other.Kind && TaskId == other.TaskId && CreatedAt == other.CreatedAt && JsonElement.DeepEquals(Payload, other.Payload);
}
