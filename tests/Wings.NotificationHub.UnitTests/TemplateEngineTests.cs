using System.Text.Json;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Templates;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.UnitTests;

public class TemplateEngineTests
{
    private static Dictionary<string, object?> Vars(object o)
        => TemplateEngine.FromJson(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o)));

    [Fact]
    public void Renders_spec_invoice_example()
    {
        const string t = "Dear {{CustomerName}},\n\nYour invoice {{InvoiceNumber}} dated {{InvoiceDate}} for ₹{{InvoiceAmount}} has been generated.\n\nRegards,\n{{CompanyName}}";
        var r = TemplateEngine.Render(t, Vars(new { CustomerName = "ABC Traders", InvoiceNumber = "INV-10045", InvoiceAmount = "25000", InvoiceDate = "2026-10-28", CompanyName = "Wings" }), OutputEncoding.Text);
        Assert.Contains("Dear ABC Traders,", r.Output);
        Assert.Contains("INV-10045 dated 2026-10-28 for ₹25000", r.Output);
        Assert.Empty(r.MissingVariables);
    }

    [Fact]
    public void Html_output_encodes_variables_but_not_template_markup()
    {
        var r = TemplateEngine.Render("<p>{{Name}}</p>", Vars(new { Name = "<script>alert(1)</script> & \"x\"" }), OutputEncoding.Html);
        Assert.StartsWith("<p>", r.Output);
        Assert.DoesNotContain("<script>", r.Output);
        Assert.Contains("&lt;script&gt;", r.Output);
        Assert.Contains("&amp;", r.Output);
    }

    [Fact]
    public void Text_output_is_not_html_encoded_and_strips_control_characters()
    {
        var r = TemplateEngine.Render("Hi {{Name}}", Vars(new { Name = "A & B\u0007" }), OutputEncoding.Text);
        Assert.Equal("Hi A & B", r.Output);
    }

    [Fact]
    public void Raw_triple_brace_output_is_rejected()
        => Assert.NotNull(TemplateEngine.Validate("{{{Body}}}"));

    [Theory]
    [InlineData("{{#if X}}open")]
    [InlineData("{{/if}}")]
    [InlineData("{{#each X}}{{/if}}")]
    [InlineData("{{Name | eval}}")]
    [InlineData("{{Name.ToString()}}")]
    [InlineData("{{#foreach X}}{{/foreach}}")]
    [InlineData("{{x")]
    public void Invalid_or_executable_syntax_is_rejected(string template)
        => Assert.NotNull(TemplateEngine.Validate(template));

    [Fact]
    public void Syntax_errors_report_line_and_column()
    {
        var ex = Assert.Throws<TemplateSyntaxException>(() => TemplateEngine.Render("line1\nline2 {{#if X}}", new Dictionary<string, object?>(), OutputEncoding.Text));
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void Missing_variables_are_reported()
    {
        var r = TemplateEngine.Render("{{A}} {{B.C}} {{D | default:'n/a'}}", Vars(new { A = 1 }), OutputEncoding.Text);
        Assert.Contains("B", r.MissingVariables);
        Assert.DoesNotContain("D", r.MissingVariables);
        Assert.EndsWith("n/a", r.Output);
    }

    [Fact]
    public void Conditionals_and_loops_render()
    {
        var vars = Vars(new { Paid = false, Items = new[] { new { Sku = "A1", Qty = 2 }, new { Sku = "B2", Qty = 5 } }, Currency = "INR" });
        var r = TemplateEngine.Render("{{#if Paid}}paid{{else}}due{{/if}};{{#unless Paid}}!{{/unless}};{{#each Items}}{{@index}}:{{Sku}}x{{this.Qty}} {{/each}}", vars, OutputEncoding.Text);
        Assert.Equal("due;!;0:A1x2 1:B2x5 ", r.Output);
    }

    [Fact]
    public void Filters_format_values()
    {
        var r = TemplateEngine.Render("{{A | currency:INR}}|{{D | date:dd-MMM-yyyy}}|{{N | upper}}|{{L | truncate:3}}|{{P | number:2}}",
            Vars(new { A = 1234567.5m, D = "2026-10-28", N = "abc", L = "abcdef", P = 3.14159 }), OutputEncoding.Text);
        Assert.Equal("₹12,34,567.50|28-Oct-2026|ABC|abc…|3.14", r.Output);
    }

    [Theory]
    [InlineData("javascript:alert(1)", "#")]
    [InlineData("data:text/html;base64,xx", "#")]
    [InlineData("https://pay.example.com/i?x=1", "https://pay.example.com/i?x=1")]
    [InlineData("/invoices/42", "/invoices/42")]
    [InlineData("//evil.example", "#")]
    public void Url_filter_only_allows_safe_schemes(string input, string expected)
        => Assert.Equal(expected, TemplateEngine.SafeUrl(input));

    [Theory]
    [InlineData("<script>x</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<iframe src=//x></iframe>")]
    public void Unsafe_template_html_is_detected(string html) => Assert.NotNull(TemplateEngine.FindUnsafeHtml(html));

    [Fact]
    public void Safe_template_html_passes() => Assert.Null(TemplateEngine.FindUnsafeHtml("<p style=\"color:red\"><a href=\"{{Link | url}}\">Pay</a></p>"));

    [Fact]
    public void Variable_usage_marks_conditional_variables_optional()
    {
        var usage = TemplateEngine.ExtractVariableUsage("{{A}} {{#if B}}{{B}} {{C}}{{/if}} {{D | default:'x'}} {{#each Items}}{{Sku}}{{/each}}").ToDictionary(x => x.Name, x => x.Required);
        Assert.True(usage["A"]);
        Assert.False(usage["B"]);
        Assert.False(usage["C"]);
        Assert.False(usage["D"]);
        Assert.True(usage["Items"]);
        Assert.False(usage.ContainsKey("Sku"));
    }

    [Fact]
    public void Html_to_text_strips_markup()
        => Assert.Equal("Hello\nWorld & co", TemplateEngine.HtmlToText("<p>Hello</p><b>World</b> &amp; co"));

    [Fact]
    public void Whatsapp_positional_conversion_and_parameters()
    {
        const string body = "Dear {{Name}}, invoice {{No}} for {{Amt | currency:INR}}. Thanks {{Name}}";
        Assert.Equal("Dear {{1}}, invoice {{2}} for {{3}}. Thanks {{1}}", ChannelRenderer.ToPositional(body));
        var v = new TemplateVersion { Body = body, ProviderTemplateName = "inv", ProviderTemplateLanguage = "en" };
        var r = ChannelRenderer.Render(v, Channel.WhatsApp, Vars(new { Name = "Ravi", No = "I-1", Amt = 1000 }));
        Assert.Equal(["Ravi", "I-1", "₹1,000.00"], r.Content.WhatsAppTemplate!.BodyParameters);
    }

    [Fact]
    public void Email_render_produces_html_and_text_alternative()
    {
        var v = new TemplateVersion { Subject = "Invoice {{No}}\r\nBcc: x", Body = "<p>Hi {{Name}}</p>", BodyFormat = BodyFormat.Html };
        var r = ChannelRenderer.Render(v, Channel.Email, Vars(new { No = "1", Name = "<b>R</b>" }), "<footer>f</footer>");
        Assert.Equal("Invoice 1 Bcc: x", r.Content.Subject); // header injection neutralised
        Assert.Contains("&lt;b&gt;R&lt;/b&gt;", r.Content.Body);
        Assert.EndsWith("<footer>f</footer>", r.Content.Body);
        Assert.Contains("Hi <b>R</b>", r.Content.TextBody);
    }

    [Fact]
    public void Sms_render_counts_segments()
    {
        var r = ChannelRenderer.Render(new TemplateVersion { Body = new string('a', 200) }, Channel.Sms, new Dictionary<string, object?>());
        Assert.Equal(2, r.Units);
    }

    [Fact]
    public void Declared_required_variables_must_be_supplied()
    {
        var v = new TemplateVersion { Body = "static", Variables = [new TemplateVariable { Name = "Ref", Required = true }] };
        Assert.Contains("Ref", ChannelRenderer.Render(v, Channel.Sms, new Dictionary<string, object?>()).Missing);
    }
}
