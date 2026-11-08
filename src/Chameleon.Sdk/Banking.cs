using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chameleon.Engine;

namespace Chameleon.Sdk;

public enum LedgerKind { Credit, Debit, Adjustment }

/// One ledger line; Amount is signed (debit negative). At is the sandbox clock.
/// Details = what the bank shows about the movement in its statement (end-to-end id, Pix description...).
public sealed record LedgerEntry(string Id, DateTimeOffset At, LedgerKind Kind, decimal Amount, string Description, string? Reference = null,
    Dictionary<string, string?>? Details = null);

/// A simulated bank account of the sandbox. Shared by every banking connector: the
/// nickname is the key and the Pix key is unique across banks. Balance = sum of the ledger;
/// setting it writes an adjustment, never overwrites.
public sealed class BankAccount : IHasNaturalKey
{
    public required string Nickname { get; init; }
    public string NaturalKeyProperty => nameof(Nickname);

    public required string Bank { get; init; }
    public required string Branch { get; set; }
    public required string Account { get; set; }
    public string? PixKey { get; set; }
    public DateTimeOffset CreatedAt { get; init; }

    // ponytail: ledger kept inside the account (one versioned row, so concurrent credits conflict
    // instead of getting lost); paginate or split out if a test account grows past a few thousand lines.
    public List<LedgerEntry> Entries { get; init; } = [];

    [JsonIgnore] public decimal Balance => Entries.Sum(e => e.Amount);
}

public sealed record OpenAccountRequest(string Nickname, string Branch, string Account, string? PixKey = null, decimal? Balance = null);

/// Absent field = unchanged; empty PixKey = removes the key.
public sealed record UpdateAccountRequest(string? Branch = null, string? Account = null, string? PixKey = null);

public static partial class Banking
{
    public static IReadOnlyCollection<BankAccount> Accounts(ConnectorStore store) => store.All<BankAccount>();

    /// Validates and checks uniqueness; null error = opened.
    public static (BankAccount? Account, string? Error, bool Conflict) Open(ConnectorContext ctx, OpenAccountRequest req)
    {
        var nickname = req.Nickname?.Trim() ?? "";
        var pixKey = string.IsNullOrWhiteSpace(req.PixKey) ? null : req.PixKey.Trim();

        if (!NicknamePattern().IsMatch(nickname)) return (null, "nickname: 1 to 40 lowercase letters, digits or hyphens", false);
        if (req.Balance is < 0) return (null, "balance cannot be negative", false);
        if (Accounts(ctx.Store).Any(a => a.Nickname == nickname)) return (null, $"account {nickname} already exists in this sandbox", true);

        var (branch, accountNumber) = (req.Branch?.Trim() ?? "", req.Account?.Trim() ?? "");
        if (Check(ctx, null, branch, accountNumber, pixKey) is { } refused) return (null, refused.Error, refused.Conflict);

        var account = new BankAccount { Nickname = nickname, Bank = ctx.Connector, Branch = branch, Account = accountNumber, PixKey = pixKey, CreatedAt = ctx.Now };
        if (req.Balance is > 0) account.Entries.Add(Entry(ctx, LedgerKind.Adjustment, Round(req.Balance.Value), "balance adjustment by the simulator"));
        ctx.Save(nickname, account);
        return (account, null, false);
    }

    /// Changes branch, account number or Pix key with the same rules as opening; balance and ledger stay.
    /// Webhook registrations are by key and do not follow a changed key.
    public static (string? Error, bool Conflict) Update(ConnectorContext ctx, BankAccount account, UpdateAccountRequest req)
    {
        var branch = req.Branch?.Trim() ?? account.Branch;
        var accountNumber = req.Account?.Trim() ?? account.Account;
        var pixKey = req.PixKey is null ? account.PixKey : string.IsNullOrWhiteSpace(req.PixKey) ? null : req.PixKey.Trim();

        if (Check(ctx, account, branch, accountNumber, pixKey) is { } refused) return refused;

        (account.Branch, account.Account, account.PixKey) = (branch, accountNumber, pixKey);
        ctx.Save(account.Nickname, account);
        return (null, false);
    }

    /// Format and uniqueness of branch, account and key against the other accounts (self = the account being edited).
    private static (string Error, bool Conflict)? Check(ConnectorContext ctx, BankAccount? self, string branch, string accountNumber, string? pixKey)
    {
        if (!BranchPattern().IsMatch(branch)) return ("branch: 4 digits", false);
        if (!AccountNumberPattern().IsMatch(accountNumber)) return ("account: up to 20 digits or hyphens", false);
        if (pixKey is { Length: > 77 }) return ("Pix key: up to 77 characters", false);

        var others = Accounts(ctx.Store).Where(a => a.Nickname != self?.Nickname).ToList();
        if (others.FirstOrDefault(a => a.Bank == ctx.Connector && a.Branch == branch && AccountCollides(a.Account, accountNumber)) is { } twin)
            return (twin.Account == accountNumber
                ? $"a {ctx.Connector} account with branch {branch} and account {accountNumber} already exists in this sandbox"
                : $"account {accountNumber} collides with {twin.Account} ({twin.Nickname}) at branch {branch}: only the check digit differs, and a lookup without the digit could not tell them apart", true);
        if (pixKey is not null && others.FirstOrDefault(a => a.PixKey == pixKey) is { } owner)
            return ($"the Pix key already belongs to account {owner.Nickname} ({owner.Bank})", true);
        return null;
    }

    /// Same account for a bank lookup that may come with or without the check digit: equal digits, or one
    /// is the other without its last digit, or both share the number and differ only in the digit.
    public static bool AccountMatches(string own, string wanted) =>
        (Digits(own).TrimStart('0'), Digits(wanted).TrimStart('0')) is var (o, w) && w.Length > 0
        && (o == w || (o.Length > 1 && o[..^1] == w) || (w.Length > 1 && w[..^1] == o));

    private static bool AccountCollides(string a, string b) =>
        AccountMatches(a, b) || (Digits(a).TrimStart('0'), Digits(b).TrimStart('0')) is var (x, y)
            && x.Length > 1 && y.Length > 1 && x[..^1] == y[..^1];

    /// Sets the balance by an adjustment line with the difference.
    public static void SetBalance(ConnectorContext ctx, BankAccount account, decimal balance)
    {
        var delta = Round(balance) - account.Balance;
        if (delta == 0) return;
        account.Entries.Add(Entry(ctx, LedgerKind.Adjustment, delta, "balance adjustment by the simulator"));
        ctx.Save(account.Nickname, account);
    }

    /// Received Pix: credits the account of this bank that owns the key. No owner: no credit
    /// (charges without registered accounts keep working). Returns the credited account.
    public static BankAccount? CreditPix(ConnectorContext ctx, string pixKey, decimal amount, string reference,
        Dictionary<string, string?>? details = null)
    {
        if (Accounts(ctx.Store).FirstOrDefault(a => a.Bank == ctx.Connector && a.PixKey == pixKey) is not { } account) return null;
        account.Entries.Add(Entry(ctx, LedgerKind.Credit, Round(amount), "Pix recebido", reference, details));
        ctx.Save(account.Nickname, account);
        return account;
    }

    /// Credits an account (received boleto, Pix to a key of the sandbox...).
    public static void Credit(ConnectorContext ctx, BankAccount account, decimal amount, string description, string? reference = null,
        Dictionary<string, string?>? details = null)
    {
        account.Entries.Add(Entry(ctx, LedgerKind.Credit, Round(amount), description, reference, details));
        ctx.Save(account.Nickname, account);
    }

    /// Debits an account if the balance covers it; false = insufficient, nothing written.
    public static bool Debit(ConnectorContext ctx, BankAccount account, decimal amount, string description, string? reference = null,
        Dictionary<string, string?>? details = null)
    {
        if (account.Balance < Round(amount)) return false;
        account.Entries.Add(Entry(ctx, LedgerKind.Debit, -Round(amount), description, reference, details));
        ctx.Save(account.Nickname, account);
        return true;
    }

    /// The bank's account an operation refers to, as banks pick it: by the account number header
    /// (digits, no leading zeros); without it, the only account of this bank in the sandbox.
    public static (BankAccount? Account, string? Error) ForBank(ConnectorContext ctx, string? accountHeader)
    {
        var mine = Accounts(ctx.Store).Where(a => a.Bank == ctx.Connector).ToList();
        if (!string.IsNullOrWhiteSpace(accountHeader))
        {
            var wanted = Digits(accountHeader).TrimStart('0');
            return mine.FirstOrDefault(a => Digits(a.Account).TrimStart('0') == wanted) is { } found
                ? (found, null)
                : (null, $"checking account {accountHeader} not found in this sandbox");
        }
        return mine.Count switch
        {
            1 => (mine[0], null),
            0 => (null, $"no {ctx.Connector} account registered in this sandbox"),
            _ => (null, "specify the checking account: there is more than one account at this bank"),
        };
    }

    public static BankAccount? ByPixKey(ConnectorStore store, string pixKey) => Accounts(store).FirstOrDefault(a => a.PixKey == pixKey);

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    public static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static LedgerEntry Entry(ConnectorContext ctx, LedgerKind kind, decimal amount, string description, string? reference = null,
        Dictionary<string, string?>? details = null) =>
        new(Guid.NewGuid().ToString("N")[..12], ctx.Now, kind, amount, description, reference, details);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    [GeneratedRegex("^[a-z0-9-]{1,40}$")] private static partial Regex NicknamePattern();
    [GeneratedRegex("^[0-9]{4}$")] private static partial Regex BranchPattern();
    [GeneratedRegex("^[0-9-]{1,20}$")] private static partial Regex AccountNumberPattern();
}
