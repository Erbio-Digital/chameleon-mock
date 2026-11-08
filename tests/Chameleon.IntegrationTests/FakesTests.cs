namespace Chameleon.IntegrationTests;

public class FakesTests
{
    [Fact]
    public void Fakes_GenerateValidDocuments()
    {
        Assert.Equal(11, Fakes.Cpf().Length);
        Assert.Equal(14, Fakes.Cnpj().Length);
        Assert.NotEqual(Fakes.Cpf(1), Fakes.Cpf(2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(999)]
    public void Fakes_HaveValidCheckDigits(int seed)
    {
        Assert.True(ValidCpf(Fakes.Cpf(seed)));
        Assert.True(ValidCnpj(Fakes.Cnpj(seed)));
    }

    private static bool ValidCpf(string s) =>
        s.Length == 11 && Dv(s[..9], 10) == s[9] - '0' && Dv(s[..10], 11) == s[10] - '0';

    private static int Dv(string digits, int weight)
    {
        var r = digits.Select((c, i) => (c - '0') * (weight - i)).Sum() * 10 % 11;
        return r == 10 ? 0 : r;
    }

    private static bool ValidCnpj(string s)
    {
        if (s.Length != 14) return false;
        int[] w1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2], w2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int Dv2(int n, int[] w) { var r = s.Take(n).Select((c, i) => (c - '0') * w[i]).Sum() % 11; return r < 2 ? 0 : 11 - r; }
        return Dv2(12, w1) == s[12] - '0' && Dv2(13, w2) == s[13] - '0';
    }
}
