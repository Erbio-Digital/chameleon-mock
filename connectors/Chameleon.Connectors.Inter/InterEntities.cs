using Chameleon.Sdk;
using Chameleon.Connectors.Pix.Common;
using Chameleon.Engine;

namespace Chameleon.Connectors.Inter;

/// Inter Pix charge: an expired charge stays ATIVA (it just can no longer be paid), so no TTL.
public sealed class InterPixCob : PixCobranca
{
    public override DateTimeOffset? DueAt => null;
}

public sealed class InterPixWebhook : PixKeyWebhook;

public sealed class InterPixDevolucao : PixDevolucao;

/// A payload location (POST /pix/v2/loc), keyed by its sequential id.
public sealed class InterLoc : IHasNaturalKey
{
    public int Id { get; init; }
    public string Key => Id.ToString();
    public string NaturalKeyProperty => nameof(Key);
    public string Location { get; init; } = "";
    public string TipoCob { get; init; } = "";
    public DateTimeOffset Criacao { get; init; }

    public PixLoc ToPix() => new(Id, Location, TipoCob, Criacao);
}

/// The account-level webhooks of the Inter (cobranca, and the banking ones), keyed by their name.
public sealed class InterAccountWebhook : PixKeyWebhook;

/// Cobranca v3 situacao (the Inter enum; PROTESTO is not simulated).
public enum SituacaoCobranca { EM_PROCESSAMENTO, FALHA_EMISSAO, A_RECEBER, ATRASADO, RECEBIDO, MARCADO_RECEBIDO, CANCELADO, EXPIRADO }

/// Boleto with Pix (Inter Cobranca v3).
public sealed class InterCobranca : IHasNaturalKey, IHasDueAt
{
    public string CodigoSolicitacao { get; init; } = "";
    public string NaturalKeyProperty => nameof(CodigoSolicitacao);

    public string SeuNumero { get; init; } = "";
    public decimal ValorNominal { get; init; }
    public DateOnly DataVencimento { get; init; }
    public int NumDiasAgenda { get; init; }
    public Pagador Pagador { get; init; } = new();
    public string? Mensagem { get; init; }

    public SituacaoCobranca Situacao { get; set; } = SituacaoCobranca.EM_PROCESSAMENTO;
    public DateTimeOffset DataSituacao { get; set; }
    public DateTimeOffset CriadoEm { get; init; }

    public string NossoNumero { get; init; } = "";
    public string CodigoBarras { get; init; } = "";
    public string LinhaDigitavel { get; init; } = "";
    public string TxId { get; init; } = "";
    public string PixCopiaECola { get; init; } = "";

    /// Sandbox account (nickname) credited on receipt; null = no account, no credit.
    public string? AccountNickname { get; init; }

    public decimal ValorTotalRecebido { get; set; }
    public string? OrigemRecebimento { get; set; }   // BOLETO | PIX
    public string? MotivoCancelamento { get; set; }


    /// Midnight (Brasilia) of the day after the due date: from then on it is ATRASADO.
    public DateTimeOffset AtrasaEm => new(DataVencimento.AddDays(1).ToDateTime(TimeOnly.MinValue), Brasilia.Offset);

    /// numDiasAgenda days after the due date it expires.
    public DateTimeOffset ExpiraEm => new(DataVencimento.AddDays(NumDiasAgenda + 1).ToDateTime(TimeOnly.MinValue), Brasilia.Offset);

    public DateTimeOffset? DueAt => Situacao switch
    {
        SituacaoCobranca.EM_PROCESSAMENTO => CriadoEm,  // issued on the next tick or advance
        SituacaoCobranca.A_RECEBER => AtrasaEm,
        SituacaoCobranca.ATRASADO => ExpiraEm,
        _ => null,
    };
}

public sealed class Pagador
{
    public string? CpfCnpj { get; set; }
    public string? TipoPessoa { get; set; }   // FISICA | JURIDICA
    public string? Nome { get; set; }
    public string? Endereco { get; set; }
    public string? Numero { get; set; }
    public string? Complemento { get; set; }
    public string? Bairro { get; set; }
    public string? Cidade { get; set; }
    public string? Uf { get; set; }
    public string? Cep { get; set; }
    public string? Email { get; set; }
    public string? Ddd { get; set; }
    public string? Telefone { get; set; }
}
