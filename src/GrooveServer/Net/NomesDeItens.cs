using System.Text;

namespace GrooveServer.Net;

/// <summary>
/// Os nomes dos itens em ingles, indexados pelos BYTES com que viajam na rede.
///
/// A notificacao do sistema (o <c>0x0039</c> do tipo 0x02) traz o nome do item dentro da frase,
/// em GBK. A tabela que o traduz — <c>pak/traducao/en/trad_icones.txt</c> — esta' em UTF-8, e o
/// .NET nao decodifica GBK sem se lhe trazer um pacote so' para isso; por isso a conversao faz-se
/// uma vez, fora, com o <c>tools/avisos_itens.py</c>, e aqui compara-se bytes com bytes.
///
/// Sem o ficheiro nao ha' estrago: a frase e' traduzida na mesma e o nome fica como veio.
/// </summary>
public static class NomesDeItens
{
    private static readonly Dictionary<string, string> PorBytes = new(StringComparer.OrdinalIgnoreCase);

    public static int Carregados => PorBytes.Count;

    public static void Carregar(string caminho)
    {
        if (!File.Exists(caminho)) return;
        foreach (var linha in File.ReadAllLines(caminho))
        {
            if (linha.Length == 0 || linha.StartsWith('#')) continue;
            var partes = linha.Split('\t');
            if (partes.Length < 2) continue;
            PorBytes[partes[0].Trim()] = partes[1].Trim();
        }
    }

    /// <summary>O nome em ingles, ou null se a tabela nao o conhecer.</summary>
    public static string? Traduzir(byte[] gbk) =>
        PorBytes.TryGetValue(Convert.ToHexString(gbk), out var n) ? n : null;
}
