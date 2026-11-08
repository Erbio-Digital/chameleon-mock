using Chameleon.Connectors.Pix.Common;

namespace Chameleon.Connectors.Bradesco;

// Bradesco's own Pix entity types (same type names as before the Pix module, so stored data still loads).
public sealed class Cobranca : PixCobranca;

public sealed class PixWebhook : PixKeyWebhook;

public sealed class Devolucao : PixDevolucao;
