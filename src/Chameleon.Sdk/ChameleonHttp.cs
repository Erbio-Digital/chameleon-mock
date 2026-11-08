using Microsoft.AspNetCore.Http;

namespace Chameleon.Sdk;

/// Lets a connector tell the capture pipeline what the current call is about, when the route
/// alone does not say it (entity key in the body, an idempotent replay, ...).
public static class ChameleonHttp
{
    public const string EntityIdKey = "chameleon.entityId";
    public const string NoteKey = "chameleon.note";

    public static void Annotate(this HttpContext http, string? entityId = null, string? note = null)
    {
        if (entityId is not null) http.Items[EntityIdKey] = entityId;
        if (note is not null) http.Items[NoteKey] = note;
    }
}
