using System.Text;

namespace GrooveServer.Protocol;

/// <summary>
/// Troca o nome do jogador dentro das mensagens gravadas.
///
/// O nome de quem gravou a sessao esta' escrito no meio dos dados — no perfil, na lista
/// de espera, no titulo da sala. Sem o substituir, qualquer conta aparece no jogo com o
/// nome de quem gravou.
///
/// A substituicao NAO DESLOCA NADA: os campos tem tamanho fixo e sao preenchidos com NUL, por
/// isso escreve-se o nome novo e enche-se o resto com zeros.
///
/// **QUANTO ESPACO HA' E' O QUE OS NULs DIZEM.** Escrevia-se no maximo o comprimento do nome
/// ANTIGO — seis, que sao as letras de "MDashK" — e por isso uma conta chamada `mariana`
/// aparecia no jogo como `marian`. O campo e' bem maior: conta-se a corrida de NULs que vem
/// logo a seguir a' ocorrencia e esse e' o espaco disponivel.
///
/// O tecto e' <see cref="MaximoDoNome"/> = 16, que e' o que o servidor original aceita ao
/// registar (6 a 16 caracteres). Nunca se escreve alem da corrida de NULs, para nao pisar o
/// campo seguinte mesmo que alguem ponha um nome maior.
/// </summary>
public sealed class NameRewriter
{
    private readonly byte[] _from;
    private readonly byte[] _to;

    public string From { get; }
    public string To { get; }

    public NameRewriter(string from, string to)
    {
        From = from; To = to;
        _from = Encoding.ASCII.GetBytes(from);
        _to = Encoding.ASCII.GetBytes(to);
    }

    /// <summary>
    /// O maior nome que o servidor original aceita. O registo pede entre 6 e 16 caracteres; o
    /// minimo nao interessa aqui — quem escolhe o nome e' o site — mas o maximo sim, porque e'
    /// ele que limita quanto se pode escrever no campo.
    /// </summary>
    public const int MaximoDoNome = 16;

    public bool IsNoOp => From == To || _from.Length == 0;

    /// <summary>Substitui no sitio; devolve quantas ocorrencias trocou.</summary>
    public int Apply(byte[] data)
    {
        if (IsNoOp) return 0;
        int trocas = 0;

        for (int i = 0; i + _from.Length <= data.Length; i++)
        {
            bool bate = true;
            for (int j = 0; j < _from.Length; j++)
                if (data[i + j] != _from[j]) { bate = false; break; }
            if (!bate) continue;

            // O espaco util e' o nome antigo mais os NULs que se lhe seguem.
            int espaco = _from.Length;
            while (i + espaco < data.Length && data[i + espaco] == 0 && espaco < MaximoDoNome) espaco++;

            int n = Math.Min(_to.Length, espaco);
            Array.Copy(_to, 0, data, i, n);
            for (int k = n; k < espaco; k++) data[i + k] = 0;

            trocas++;
            i += _from.Length - 1;
        }
        return trocas;
    }
}
