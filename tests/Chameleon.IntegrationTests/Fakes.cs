namespace Chameleon.IntegrationTests;

/// Synthetic Brazilian documents with valid check digits, deterministic per seed.
public static class Fakes
{
    public static string Cpf(int seed = 1)
    {
        var d = Digits(seed, 9);
        d.Add(Check(d, 10));
        d.Add(Check(d, 11));
        return string.Concat(d);
    }

    public static string Cnpj(int seed = 1)
    {
        var d = Digits(seed, 8);
        d.AddRange([0, 0, 0, 1]);
        d.Add(CheckCnpj(d, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));
        d.Add(CheckCnpj(d, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));
        return string.Concat(d);
    }

    private static List<int> Digits(int seed, int n) =>
        Enumerable.Range(0, n).Select(i => (seed * 7 + i * 3 + 1) % 10).ToList();

    private static int Check(List<int> d, int w)
    {
        var r = d.Select((x, i) => x * (w - i)).Sum() * 10 % 11;
        return r == 10 ? 0 : r;
    }

    private static int CheckCnpj(List<int> d, int[] w)
    {
        var r = d.Select((x, i) => x * w[i]).Sum() % 11;
        return r < 2 ? 0 : 11 - r;
    }
}
