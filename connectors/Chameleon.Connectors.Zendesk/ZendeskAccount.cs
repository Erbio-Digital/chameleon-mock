namespace Chameleon.Connectors.Zendesk;

/// The admin configuration of a synthetic sample account (Acme): one ticket form and a few custom fields.
/// In the real provider it lives in the Zendesk admin; here it is fixed. The ids are made up.
internal static class ZendeskAccount
{
    public const long AccountId = 1000001;

    public const long RequesterName = 910000000001;
    public const long RequesterDocument = 910000000002;
    public const long RequesterPhone = 910000000003;
    public const long Category = 910000000004;
    public const long CaseStatus = 910000000005;
    public const long OrderId = 910000000006;
    public const long Amount = 910000000007;

    public const string CaseStatusPrefix = "case_status_";
    public const string RefundTag = "refund_done";

    /// Option fields: the value is an option code that Zendesk mirrors as a tag.
    public static readonly HashSet<long> Dropdowns = [Category, CaseStatus];

    /// Fields holding personal data: masked in the history.
    public static readonly string[] Personal = [$"{RequesterName}", $"{RequesterDocument}", $"{RequesterPhone}"];
}
