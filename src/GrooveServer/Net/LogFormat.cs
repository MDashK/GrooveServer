namespace GrooveServer.Net;

/// <summary>
/// O prefixo de cada linha de registo de uma sessao: <c>[dd/mm/aaaa hh:mm:ss | sessao N]</c>.
///
/// A hora esta' la' porque um log de tarde de testes tem meia duzia de sessoes seguidas —
/// entrar, jogar, sair, voltar a entrar — e sem data e hora nao ha' maneira de dizer qual
/// e' a que corresponde ao que se acabou de ver no ecra.
/// </summary>
internal static class LogFormat
{
    /// <summary>
    /// Cultura invariante de proposito: o <c>/</c> e o <c>:</c> num formato de data sao
    /// "separador do sitio", nao caracteres literais, e mudariam com a maquina.
    /// </summary>
    public static string Prefixo(string quem) =>
        "[" + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss",
                                    System.Globalization.CultureInfo.InvariantCulture) +
        $" | {quem}]";

    public static string Sessao(int id) => Prefixo($"session {id}");
}
