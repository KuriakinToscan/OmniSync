using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniSync.Cli;

/// <summary>
/// Ação pedida na linha de comando.
/// </summary>
public enum CliAction
{
    /// <summary>Mostra a ajuda.</summary>
    Help,
    /// <summary>Lista as tarefas salvas.</summary>
    List,
    /// <summary>Só analisa a tarefa, sem alterar nada.</summary>
    Analyze,
    /// <summary>Analisa e sincroniza a tarefa.</summary>
    Run,
    /// <summary>Mostra a versão do executável.</summary>
    Version
}

/// <summary>
/// O que fazer com conflitos sem decisão quando não há ninguém para perguntar.
/// </summary>
public enum CliConflictPolicy
{
    /// <summary>Mantém sempre o arquivo do destino (padrão).</summary>
    Keep,
    /// <summary>Substitui só quando a origem é mais nova; destino mais novo ou mesmo horário são mantidos.</summary>
    ReplaceWhenSourceNewer
}

/// <summary>
/// Códigos de saída do modo de linha de comando, usados por scripts e pelo agendador.
/// </summary>
public enum CliExitCode
{
    /// <summary>Concluído sem falhas.</summary>
    Success = 0,
    /// <summary>Concluído com uma ou mais falhas de arquivo.</summary>
    Failures = 1,
    /// <summary>Abortado pela proteção contra remoção em massa; nada foi alterado.</summary>
    MassRemovalAborted = 2,
    /// <summary>Argumento, tarefa ou caminho inválido; nada foi alterado.</summary>
    InvalidInput = 3,
    /// <summary>Interrompido (Ctrl+C); nenhum arquivo parcial fica no destino.</summary>
    Cancelled = 4,
    /// <summary>Erro inesperado (detalhes no log).</summary>
    UnexpectedError = 5,
    /// <summary>A mesma tarefa já está sendo sincronizada (pela interface ou por outro agendamento); nada foi feito.</summary>
    AlreadyRunning = 6,
    /// <summary>
    /// Conexão remota pendente de você: falta a senha no cofre, o servidor recusou a senha guardada, ou a chave do
    /// servidor é nova ou mudou. Nada foi feito; abra o OmniSync e analise a tarefa uma vez.
    /// </summary>
    RemotePending = 7,
    /// <summary>Servidor remoto inacessível (endereço, porta, rede) ou arquivo de chave ilegível; nada foi feito.</summary>
    RemoteUnavailable = 8
}

/// <summary>
/// Comando de linha de comando já interpretado.
/// </summary>
/// <param name="Action">Ação pedida.</param>
/// <param name="TaskName">Nome (ou identificador) da tarefa, para analisar e sincronizar.</param>
/// <param name="ConflictPolicy">Política para conflitos sem decisão.</param>
/// <param name="ReportPath">Arquivo CSV a gravar com o plano e o resultado, ou <c>null</c>.</param>
/// <param name="Verbose">Se todas as linhas do registro vão também para o console.</param>
/// <param name="Scheduled">Execução disparada pelo agendador: registra a origem no histórico e mostra o aviso ao fim.</param>
public sealed record CliCommand(
    CliAction Action,
    string? TaskName = null,
    CliConflictPolicy ConflictPolicy = CliConflictPolicy.Keep,
    string? ReportPath = null,
    bool Verbose = false,
    bool Scheduled = false)
{
    private static readonly string[] HelpTokens = { "--ajuda", "--help", "-h", "/?" };
    private static readonly string[] ListTokens = { "--listar", "--list" };
    private static readonly string[] AnalyzeTokens = { "--analisar", "--analyze" };
    private static readonly string[] RunTokens = { "--sincronizar", "--run" };
    private static readonly string[] VersionTokens = { "--versao", "--versão", "--version" };

    /// <summary>
    /// Indica se os argumentos pedem o modo de linha de comando (em vez de abrir a janela): qualquer argumento
    /// que não seja <c>--self-test</c>, tratado pela interface. Assim um comando digitado errado mostra o erro
    /// no console em vez de abrir a janela.
    /// </summary>
    /// <param name="args">Argumentos do processo.</param>
    public static bool IsCliInvocation(IReadOnlyList<string> args) =>
        args.Any(a => !string.Equals(a, SelfTestToken, StringComparison.OrdinalIgnoreCase));

    /// <summary>Argumento do autoteste da interface.</summary>
    public const string SelfTestToken = "--self-test";

    /// <summary>
    /// Interpreta os argumentos.
    /// </summary>
    /// <param name="args">Argumentos do processo.</param>
    /// <param name="command">Comando interpretado, quando válido.</param>
    /// <param name="error">Mensagem em português quando os argumentos são inválidos.</param>
    /// <returns><c>true</c> se os argumentos são válidos.</returns>
    public static bool TryParse(IReadOnlyList<string> args, out CliCommand? command, out string? error)
    {
        command = null;
        error = null;
        CliAction? action = null;
        string? taskName = null;
        var policy = CliConflictPolicy.Keep;
        string? report = null;
        bool verbose = false;
        bool scheduled = false;

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (Matches(arg, HelpTokens))
            {
                command = new CliCommand(CliAction.Help);
                return true;
            }

            if (Matches(arg, VersionTokens))
            {
                command = new CliCommand(CliAction.Version);
                return true;
            }

            if (Matches(arg, ListTokens) || Matches(arg, AnalyzeTokens) || Matches(arg, RunTokens))
            {
                if (action != null)
                {
                    error = "Informe só um comando: --listar, --analisar ou --sincronizar.";
                    return false;
                }
                if (Matches(arg, ListTokens))
                {
                    action = CliAction.List;
                    continue;
                }
                action = Matches(arg, AnalyzeTokens) ? CliAction.Analyze : CliAction.Run;
                if (!TryValue(args, ref i, out taskName))
                {
                    error = $"Falta o nome da tarefa depois de {arg}. Exemplo: {arg} \"Volume\"";
                    return false;
                }
            }
            else if (Matches(arg, new[] { "--conflitos", "--conflicts" }))
            {
                if (!TryValue(args, ref i, out var value))
                {
                    error = "Falta o valor de --conflitos: manter ou origem-mais-nova.";
                    return false;
                }
                switch (value!.Trim().ToLowerInvariant())
                {
                    case "manter":
                        policy = CliConflictPolicy.Keep;
                        break;
                    case "origem-mais-nova":
                        policy = CliConflictPolicy.ReplaceWhenSourceNewer;
                        break;
                    default:
                        error = $"Valor inválido para --conflitos: \"{value}\". Use manter ou origem-mais-nova.";
                        return false;
                }
            }
            else if (Matches(arg, new[] { "--relatorio", "--relatório", "--report" }))
            {
                if (!TryValue(args, ref i, out report))
                {
                    error = "Falta o caminho do arquivo depois de --relatorio. Exemplo: --relatorio C:\\Temp\\volume.csv";
                    return false;
                }
            }
            else if (Matches(arg, new[] { "--detalhado", "--verbose" }))
            {
                verbose = true;
            }
            else if (Matches(arg, new[] { "--agendado", "--scheduled" }))
            {
                scheduled = true;
            }
            else
            {
                error = $"Argumento desconhecido: {arg}. Use --ajuda para ver as opções.";
                return false;
            }
        }

        if (action == null)
        {
            error = "Nenhum comando informado. Use --listar, --analisar, --sincronizar ou --ajuda.";
            return false;
        }

        if (scheduled && action == CliAction.List)
        {
            error = "--agendado só vale com --analisar ou --sincronizar.";
            return false;
        }

        command = new CliCommand(action.Value, taskName, policy, report, verbose, scheduled);
        return true;
    }

    /// <summary>
    /// Texto de ajuda exibido por <c>--ajuda</c>.
    /// </summary>
    public const string HelpText =
        """
        OmniSync - modo de linha de comando

        Uso:
          OmniSync --listar
          OmniSync --analisar "Tarefa" [--relatorio arquivo.csv] [--detalhado]
          OmniSync --sincronizar "Tarefa" [--conflitos manter|origem-mais-nova] [--relatorio arquivo.csv] [--detalhado]
          OmniSync --versao

        Comandos:
          --listar        Lista as tarefas salvas na interface.
          --analisar      Só analisa: mostra novos, conflitos e órfãos, sem alterar nada.
          --sincronizar   Analisa e sincroniza a tarefa.
          --versao        Mostra a versão do OmniSync.

        Opções:
          --conflitos     manter (padrão): mantém o arquivo do destino em todo conflito.
                          origem-mais-nova: substitui só quando a origem é mais nova; a versão
                          anterior vai para old/. Destino mais novo é sempre mantido.
          --relatorio     Grava o CSV do plano (ao analisar) ou do resultado (ao sincronizar).
          --detalhado     Mostra no console todas as linhas do registro, arquivo por arquivo.
          --agendado      Usada pelo agendamento: registra a execução como agendada e mostra
                          uma janela de aviso ao fim (só quando há algo a avisar).

        Segurança: se a análise indicar remoção em massa (muitos órfãos para lixo/), a sincronização
        é abortada sem alterar nada. Revise a tarefa pela interface.

        Códigos de saída: 0 sem falhas; 1 com falhas; 2 abortado por remoção em massa;
        3 argumento, tarefa ou caminho inválido; 4 interrompido; 5 erro inesperado;
        6 a tarefa já está sendo sincronizada; 7 conexão remota pendente de você (senha ou
        chave do servidor: abra o OmniSync e analise a tarefa uma vez); 8 servidor remoto inacessível.

        No PowerShell, use "| Out-Host" para esperar o fim e ver a saída:
          .\OmniSync.exe --sincronizar "Volume" | Out-Host ; $LASTEXITCODE
        """;

    private static bool Matches(string arg, IEnumerable<string> tokens) =>
        tokens.Any(t => string.Equals(arg, t, StringComparison.OrdinalIgnoreCase));

    private static bool TryValue(IReadOnlyList<string> args, ref int i, out string? value)
    {
        if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[i + 1]))
        {
            value = args[++i];
            return true;
        }
        value = null;
        return false;
    }
}
