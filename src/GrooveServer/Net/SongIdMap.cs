namespace GrooveServer.Net;

/// <summary>
/// Traduz o id de uma musica entre clientes de versoes diferentes.
///
/// **O id de rede e' a POSICAO da musica no `Song\DiscStock.csv` do cliente** — o CSV numera de
/// 1 e a rede de 0. Como cada versao traz o seu catalogo, o mesmo numero refere musicas
/// diferentes: o cliente SNDA de 2007 tem 94 musicas e o chines de 2019 tem 277, e das 94 do
/// SNDA **nenhuma** cai no mesmo id.
///
/// Sem isto o servidor recebe "quero a musica 3" de um cliente SNDA (que para ele e' o
/// `dplanet`), vai buscar o chart 3 da nossa biblioteca (que e' o `Alliwant`) e devolve-lho. O
/// cliente aceita o bloco — sao as duas charts validas — mas toca as notas de uma musica com o
/// audio e o video de outra. Era isso o "nao ha' som nem video".
///
/// O que casa os dois catalogos e' o `Tag`: o nome do `.pak` da musica, que e' o ficheiro que o
/// cliente abre para o audio. Ver `re/gerar_mapa_snda.py`.
///
/// O ficheiro e' `id_do_cliente &lt;TAB&gt; id_da_biblioteca &lt;TAB&gt; tag`, com `#` a comentar.
/// </summary>
public sealed class SongIdMap
{
    private readonly Dictionary<uint, uint> _paraBiblioteca = new();
    private readonly Dictionary<uint, uint> _paraCliente = new();

    public int Count => _paraBiblioteca.Count;

    /// <summary>O id que a biblioteca conhece. Sem traducao conhecida devolve o que veio.</summary>
    public uint ParaBiblioteca(uint doCliente) =>
        _paraBiblioteca.TryGetValue(doCliente, out var v) ? v : doCliente;

    /// <summary>O caminho inverso, para quando o servidor tem de nomear uma musica ao cliente.</summary>
    public uint ParaCliente(uint daBiblioteca) =>
        _paraCliente.TryGetValue(daBiblioteca, out var v) ? v : daBiblioteca;

    public bool Conhece(uint doCliente) => _paraBiblioteca.ContainsKey(doCliente);

    public static SongIdMap Load(string path)
    {
        var m = new SongIdMap();
        if (!File.Exists(path)) return m;

        int linha = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            linha++;
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith('#')) continue;

            var campos = s.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (campos.Length < 2 ||
                !uint.TryParse(campos[0].Trim(), out uint doCliente) ||
                !uint.TryParse(campos[1].Trim(), out uint daBiblioteca))
            {
                Console.WriteLine($"  ({Path.GetFileName(path)} line {linha}: " +
                                  "needs two numbers separated by TAB)");
                continue;
            }
            m._paraBiblioteca[doCliente] = daBiblioteca;
            // O inverso so' guarda a PRIMEIRA: se duas do cliente apontassem a' mesma da
            // biblioteca, nomear de volta seria ambiguo e mais vale ficar pela primeira.
            m._paraCliente.TryAdd(daBiblioteca, doCliente);
        }
        return m;
    }
}
