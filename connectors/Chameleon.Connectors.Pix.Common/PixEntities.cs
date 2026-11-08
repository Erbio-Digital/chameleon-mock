using System.Text.Json;
using System.Text.Json.Serialization;
using Chameleon.Engine;

namespace Chameleon.Connectors.Pix.Common;

/// Real Pix immediate-charge status enum (Bacen API Pix, /v2/cob).
public enum CobrancaStatus { ATIVA, CONCLUIDA, REMOVIDA_PELO_USUARIO_RECEBEDOR, REMOVIDA_PELO_PSP }

/// The simulated Pix charge, mirroring the Bacen /v2/cob resource. Each connector declares its own
/// subclass: the store keeps entities by type, so the same txid at two banks does not collide.
public class PixCobranca : IHasNaturalKey, IHasDueAt
{
    public string TxId { get; init; } = "";             // path param, 26-35 alphanumeric
    public string NaturalKeyProperty => nameof(TxId);

    public decimal ValorOriginal { get; set; }           // valor.original
    public int ModalidadeAlteracao { get; set; }         // valor.modalidadeAlteracao: 1 = the payer may change the amount
    public string Chave { get; init; } = "";             // recebedor Pix key
    public int ExpiracaoSegundos { get; set; } = 3600;   // calendario.expiracao
    public Devedor? Devedor { get; set; }
    public string? SolicitacaoPagador { get; set; }

    public int Revisao { get; set; }                     // increments on each revision/state change
    public CobrancaStatus Status { get; set; } = CobrancaStatus.ATIVA;
    public string PixCopiaECola { get; init; } = "";     // EMV BR Code payload
    public DateTimeOffset CriacaoEm { get; init; }
    public DateTimeOffset ExpiraEm { get; set; }

    // cobv only (null = cob): calendario.dataDeVencimento, validadeAposVencimento and the valor charges echoed as sent.
    public DateOnly? DataDeVencimento { get; init; }
    public int ValidadeAposVencimento { get; init; }
    public Dictionary<string, JsonElement>? Encargos { get; init; }
    public PixLoc? Loc { get; init; }

    /// Where the PSP removes an active charge at expiration (Bradesco); null keeps it ATIVA (Inter).
    public virtual DateTimeOffset? DueAt => Status == CobrancaStatus.ATIVA ? ExpiraEm : null;

    // Set when paid (CONCLUIDA): the received Pix leg.
    public string? EndToEndId { get; set; }
    public DateTimeOffset? PagoEm { get; set; }
    public decimal? ValorPago { get; set; }              // pix[].valor; null = the original
    public Devedor? Pagador { get; set; }                // pix[].pagador
    public string? InfoPagador { get; set; }             // pix[].infoPagador

    /// Sandbox account (nickname) the charge belongs to, resolved from x-conta-corrente when the bank tells it.
    public string? AccountNickname { get; set; }

    // Simulator-only fallback when the Pix key has no webhook configured. Not a Pix field.
    public string? WebhookUrl { get; init; }
}

public sealed class Devedor
{
    public string? Cpf { get; set; }
    public string? Cnpj { get; set; }
    public string? Nome { get; set; }

    // cobv debtor address (Bacen), echoed only when sent.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Email { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Logradouro { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Cidade { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Uf { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Cep { get; set; }
}

/// A Pix payload location (Bacen /v2/loc), as a cobv embeds it.
public sealed record PixLoc(int Id, string Location, string TipoCob, DateTimeOffset Criacao);

public enum DevolucaoStatus { EM_PROCESSAMENTO, DEVOLVIDO, NAO_REALIZADO }

/// A refund of a received Pix (Bacen /v2/pix/{e2eid}/devolucao/{id}); each bank declares its subclass.
public class PixDevolucao : IHasNaturalKey
{
    public string Chave { get; init; } = "";             // {e2eid}_{id}
    public string NaturalKeyProperty => nameof(Chave);

    public string EndToEndId { get; init; } = "";
    public string Id { get; init; } = "";
    public string RtrId { get; init; } = "";
    public string TxId { get; init; } = "";              // the charge whose Pix is refunded
    public decimal Valor { get; init; }
    public DevolucaoStatus Status { get; set; } = DevolucaoStatus.EM_PROCESSAMENTO;
    public string? Motivo { get; set; }
    public DateTimeOffset Solicitacao { get; init; }
    public DateTimeOffset? Liquidacao { get; set; }
}

/// The webhook a Pix key notifies (PUT /v2/webhook/{chave}), kept per sandbox. Null URL = removed.
public class PixKeyWebhook : IHasNaturalKey
{
    public string Chave { get; init; } = "";
    public string NaturalKeyProperty => nameof(Chave);
    public string? WebhookUrl { get; init; }
    public DateTimeOffset Criacao { get; init; }
}
