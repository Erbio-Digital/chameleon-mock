using Chameleon.Engine;

namespace Chameleon.Connectors.NfseSaoPaulo;

public enum NfseState { Recebido, Processando, Autorizada, Rejeitada }

/// The simulated NFS-e. Its lifecycle (receive -> process -> authorize/reject) is what
/// makes this a simulator, not a mock. Natural key is the RPS number.
public sealed class Nfse : IHasNaturalKey, IHasDueAt
{
    public required string Rps { get; init; }
    public string NaturalKeyProperty => nameof(Rps);
    public required decimal Valor { get; init; }
    public string? WebhookUrl { get; init; }
    public string? Protocolo { get; set; }
    public string? MotivoRejeicao { get; set; }
    public NfseState State { get; set; } = NfseState.Recebido;
    public DateTimeOffset RecebidaEm { get; init; }
    public DateTimeOffset ProcessaAte { get; init; }

    /// A note still processing is authorized when processing time is over.
    public DateTimeOffset? DueAt => State == NfseState.Processando ? ProcessaAte : null;
}
