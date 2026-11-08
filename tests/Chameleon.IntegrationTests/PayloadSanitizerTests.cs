using Chameleon.Engine;

namespace Chameleon.IntegrationTests;

/// LGPD guard of the journal and views: fields are classified by name pattern in every format.
public sealed class PayloadSanitizerTests
{
    [Fact]
    public void Json_masks_personal_and_address_fields_and_redacts_secrets_by_pattern()
    {
        var body = PayloadSanitizer.Body("""
            {"pagador":{"cpfCnpj":"12345678909","nome":"Maria Souza","endereco":"Rua A","cep":"01001000","numero":"10"},
             "refresh_token":"rt-123","apiKey":"k-123","valor":"10.00","excecao":"x","webhookUrl":"https://app/hook?api_key=k-999"}
            """).Body!;

        Assert.DoesNotContain("12345678909", body);
        Assert.DoesNotContain("Maria Souza", body);
        Assert.DoesNotContain("Rua A", body);
        Assert.DoesNotContain("01001000", body);
        Assert.DoesNotContain("rt-123", body);
        Assert.DoesNotContain("k-123", body);
        Assert.DoesNotContain("k-999", body);
        Assert.Contains("\"valor\":\"10.00\"", body);
        Assert.Contains("\"excecao\":\"x\"", body);
    }

    [Fact] // Bradesco SPI: bank details are personal financial data (LGPD), numbers included
    public void Json_masks_bank_details_and_receiver_fields()
    {
        var body = PayloadSanitizer.Body("""
            {"idtransacao":"abc","pagador":{"agencia":"0001","conta":"0012345"},
             "recebedor":{"cpfCnpj":"12345678909","nomeFavorecido":"Maria Souza","agencia":"1234","conta":"98765","digitoConta":"4"},
             "contaRemetenteComDigito":123456,"valor":"80.00"}
            """).Body!;

        Assert.DoesNotContain("0012345", body);
        Assert.DoesNotContain("0001", body);
        Assert.DoesNotContain("98765", body);
        Assert.DoesNotContain("123456", body);
        Assert.DoesNotContain("12345678909", body);
        Assert.DoesNotContain("Maria Souza", body);
        Assert.Contains("\"valor\":\"80.00\"", body);
        Assert.Contains("\"idtransacao\":\"abc\"", body);
    }

    [Fact] // Bradesco TED: the receiver's CNPJ goes split in three fields
    public void Json_masks_the_split_cnpj_of_a_ted()
    {
        var body = PayloadSanitizer.Body("""
            {"numeroInscricao":"123456789","numeroFilial":"00024","numeroControle":"81","nomeClienteDestinatario":"ACME LTDA","valorDaTransferencia":10.5}
            """).Body!;

        Assert.DoesNotContain("123456789", body);
        Assert.DoesNotContain("00024", body);
        Assert.DoesNotContain("\"81\"", body);
        Assert.DoesNotContain("ACME LTDA", body);
        Assert.Contains("\"valorDaTransferencia\":10.5", body);
    }

    [Fact]
    public void Xml_matches_tags_that_contain_the_field_name()
    {
        var body = PayloadSanitizer.Body("<RPS><RazaoSocialTomador>ACME LTDA</RazaoSocialTomador><EmailTomador>a@example.com</EmailTomador>"
            + "<CPFCNPJTomador><CNPJ>11222333000181</CNPJ></CPFCNPJTomador><Valor>1.00</Valor></RPS>").Body!;

        Assert.DoesNotContain("ACME LTDA", body);
        Assert.DoesNotContain("a@example.com", body);
        Assert.DoesNotContain("11222333000181", body);
        Assert.Contains("<Valor>1.00</Valor>", body);
    }

    [Fact]
    public void Malformed_json_still_has_its_fields_masked()
    {
        var body = PayloadSanitizer.Body("""{"cpf":"12345678909","client_secret":"s-1",""").Body!;
        Assert.DoesNotContain("12345678909", body);
        Assert.DoesNotContain("s-1", body);
    }

    [Fact]
    public void Query_url_and_headers_hide_what_may_identify_or_authenticate()
    {
        Assert.Equal("?cpf=12%2A%2A%2A%2A%2A%2A%2A09&page=1", PayloadSanitizer.Query("?cpf=12345678909&page=1"));
        Assert.Equal("https://app.qa/hook?ApiKey=[redacted]", PayloadSanitizer.Url("https://user:pw@app.qa/hook?ApiKey=abc"));

        var headers = PayloadSanitizer.Headers([
            new("auth-key", "a"), new("X-Auth-Token", "b"), new("client-secret", "c"), new("x-hub-signature-256", "d"), new("Content-Type", "e")]);
        Assert.All(headers.Where(h => h.Key != "Content-Type"), h => Assert.Equal(PayloadSanitizer.Redacted, h.Value));
        Assert.Equal("e", headers["Content-Type"]);
    }

    [Fact] // Zendesk custom fields: the value of a declared id is personal, the others stay readable
    public void Json_masks_the_value_of_declared_field_ids()
    {
        PayloadSanitizer.MaskFieldIds("900000000001");
        var body = PayloadSanitizer.Body("""
            {"custom_fields":[{"id":900000000001,"value":"12345678000190"},{"id":900000000002,"value":"case_status_accepted"}]}
            """).Body!;

        Assert.DoesNotContain("12345678000190", body);
        Assert.Contains("case_status_accepted", body);
    }

    [Fact]
    public void Free_text_keeps_the_text_and_masks_emails_and_documents_inside()
    {
        var body = PayloadSanitizer.Body("""
            {"comment":{"body":"Contact maria.souza@example.com, CPF 123.456.789-09, CNPJ 12345678000190, order ORD-1001"}}
            """).Body!;

        Assert.DoesNotContain("maria.souza@example.com", body);
        Assert.DoesNotContain("123.456.789-09", body);
        Assert.DoesNotContain("12345678000190", body);
        Assert.Contains("Contact ", body);
        Assert.Contains("order ORD-1001", body);
    }

    [Fact]
    public void Only_text_content_types_are_read_as_text()
    {
        Assert.True(PayloadSanitizer.IsText(null));
        Assert.True(PayloadSanitizer.IsText("application/json; charset=utf-8"));
        Assert.True(PayloadSanitizer.IsText("application/x-www-form-urlencoded"));
        Assert.False(PayloadSanitizer.IsText("application/octet-stream"));
        Assert.False(PayloadSanitizer.IsText("image/png"));
        Assert.Equal("<binary 10 bytes>", PayloadSanitizer.Binary(10));
    }
}
