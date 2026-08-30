using GrooveServer.Pak;

namespace GrooveServer.Tools;

/// <summary>
/// O comando <c>pak</c> do servidor. A implementacao esta' em <see cref="PakCli"/>, partilhada
/// com o <b>reXIP</b> — o mesmo codigo, num executavel a' parte (src/reXIP) para quem so'
/// quer mexer nos ficheiros do jogo e nao tem nada que ver com o servidor.
/// </summary>
public static class PakTool
{
    public static void Run(string[] args) => new PakCli
    {
        Comando = "pak",
        PastaChaves = GrooveServer.Config.Chaves,
        PastaJogo = GrooveServer.Config.PastaJogo,
        SabeOndeEOJogo = true,   // o servidor tem a pasta do jogo na configuracao
    }.Run(args);
}
