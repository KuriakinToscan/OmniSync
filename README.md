# OmniSync — Sincronizador de Arquivos Multiplataforma

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia_UI-12.1-0d6efd.svg)](https://avaloniaui.net/)
[![Platform](https://img.shields.io/badge/Plataforma-Windows%20%7C%20Linux-success.svg)](#)

O **OmniSync** é uma aplicação desktop multiplataforma (**Windows e Linux**) com interface gráfica nativa construída em **.NET 10** e **Avalonia UI**, projetada para sincronizar diretórios entre unidades locais (SSDs, HDs externos, pendrives) e compartilhamentos de rede (**NAS / SMB / CIFS / NFS**) com foco em **segurança contra perda de dados**.

---

## ✨ Regras de Negócio e Diferenciais

Diferente de ferramentas tradicionais que apagam ou sobrescrevem arquivos irreversivelmente, o **OmniSync** opera em duas etapas claras (**Análise Dry-Run** seguida de **Execução**) e garante zero perda acidental:

1. **Arquivos Idênticos (Ignorados)**: Arquivos presentes na Origem e no Destino com o mesmo tamanho e data de modificação (ou mesmo hash **SHA-256**) não são copiados novamente.
2. **Arquivos Novos (Cópia Incremental)**: Arquivos que existem na Origem e ainda não existem no Destino são copiados preservando toda a hierarquia de subpastas e o timestamp original.
3. **Arquivos Órfãos (`destino/lixo/`)**: Arquivos que não existem mais na Origem, mas permanecem no Destino, **nunca são excluídos** — são movidos para `<Destino>/lixo/<caminho_relativo>` e podem ser restaurados seletivamente, sem sobrescrever nada.
4. **Arquivos Conflitantes (`destino/old/`)**: Quando um arquivo existe nos dois lados com conteúdo diferente, a prévia indica qual lado é mais recente. A decisão (**Substituir**, **Ignorar** ou **Mais recente vence**) pode ser tomada na própria tabela antes de sincronizar, ou em um modal comparativo durante a execução. Ao substituir, a versão anterior é preservada em `<Destino>/old/<caminho_relativo>`.
5. **Subpasta Automática por Nome da Origem**: Ao selecionar uma pasta de origem (ex.: `E:\Projetos`) e apontar para a raiz de outro drive (ex.: `G:\`), o OmniSync direciona automaticamente a sincronização para `G:\Projetos`, protegendo as demais pastas do disco de destino. O campo Destino guarda só a pasta escolhida; a linha **Sincroniza em** mostra o destino efetivo.
6. **Escrita atômica e confirmação prévia**: cada cópia é gravada em arquivo temporário e renomeada ao final (cancelamentos e quedas de rede não deixam arquivos truncados). Antes de executar, uma janela resume o plano e exige confirmação explícita quando grande parte do destino iria para `lixo/`.
7. **Auditoria**: log diário completo em arquivo, janela **Registro** com filtro e cópia, e exportação do plano/resultado em CSV (cabeçalho ASCII, com colunas de código). Erros do sistema aparecem em português; a mensagem original fica no log.
8. **Tarefas salvas**: cada combinação de origem, destino e opções fica salva na barra lateral, com o estado da última execução. Trocar de tarefa carrega a configuração e descarta o plano anterior.
9. **Decisões seguras**: nenhum conflito vem pré-selecionado; decisões tomadas voltam após uma nova análise se os arquivos não mudaram; substituir uma versão mais nova do destino exige confirmação própria na revisão; origem e destino iguais ou aninhados são apontados na hora.
10. **Agendamento e instalação**: cada tarefa pode rodar sozinha (todo dia, em dias da semana ou ao entrar no sistema), pelo Agendador de Tarefas do Windows ou pelo systemd no Linux, sincronizando ou só analisando e avisando. Ao abrir, o OmniSync oferece instalar-se numa pasta fixa do usuário, com atalhos na área de trabalho e no menu Iniciar.

### Interface

A janela segue o fluxo da sincronização em quatro telas:

1. **Configuração da tarefa**: origem, destino e opções; a análise só lê as pastas.
2. **Plano analisado**: barra proporcional com filtros por categoria, árvore de **pastas com alterações**, lista de arquivos e painel de detalhes, onde cada conflito é decidido sem diálogo. A barra inferior resume o que vai acontecer.
3. **Execução**: progresso total e por fase (cópias, conflitos, `lixo/`), velocidade, tempo restante e registro com filtro de avisos e erros.
4. **Resultado**: balanço, falhas com o motivo, nova tentativa e atalhos para `lixo/`, `old/` e o relatório CSV.

Em janelas com menos de 1.240 px de largura, a árvore de pastas é ocultada para preservar a lista e o painel de detalhes.

### Linha de comando

As tarefas salvas também rodam sem janela e sem perguntas, com código de saída para scripts e agendador:

```powershell
.\OmniSync.exe --listar | Out-Host
.\OmniSync.exe --analisar "Volume" | Out-Host
.\OmniSync.exe --sincronizar "Volume" --relatorio C:\Temp\volume.csv | Out-Host ; $LASTEXITCODE
```

Conflitos sem decisão mantêm o destino (ou, com `--conflitos origem-mais-nova`, só substituem quando a origem é mais nova) e remoção em massa aborta sem alterar nada. Detalhes no [manual](docs/MANUAL_DO_USUARIO.md#9-linha-de-comando-sem-janela). O agendamento usa esses mesmos comandos; veja [Instalação e Agendamento](docs/MANUAL_DO_USUARIO.md#10-instalação-e-agendamento).

---

## 📚 Documentação Completa

- 📖 **[Manual do Usuário](docs/MANUAL_DO_USUARIO.md)** — Guia passo a passo de instalação, configuração de pastas locais e NAS, resolução de conflitos e restauração de arquivos.
- 🛠️ **[Documentação Técnica e de Desenvolvimento](docs/DOCUMENTACAO_TECNICA.md)** — Arquitetura MVVM, funcionamento interno do `SyncEngine`, concorrência, tratamento de erros e instruções de build/testes.

---

## 🚀 Como Executar e Compilar

### Pré-requisitos (para compilar a partir do código-fonte)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows x64 ou Linux x64)

### 1. Executar em modo de desenvolvimento
```bash
dotnet run --project OmniSync.csproj
```

### 2. Executar a suíte de testes automatizados
```bash
# Testes unitários e de integração (xUnit)
dotnet test Tests/OmniSync.Tests.csproj

# Autoteste de ponta a ponta da interface gráfica (resultado em selftest.log na pasta de dados do usuário)
dotnet run --project OmniSync.csproj -- --self-test
```

### 3. Gerar executável único (Standalone Single-File)

**No Windows (`win-x64`):**
```powershell
dotnet publish OmniSync.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish/win-x64
```
O executável final estará em `publish/win-x64/OmniSync.exe` (também acionável pelo atalho `Iniciar OmniSync.bat`).

**No Linux (`linux-x64`):**
```bash
dotnet publish OmniSync.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish/linux-x64
chmod +x publish/linux-x64/OmniSync
```

---

## 🗂️ Estrutura do Projeto

```text
OmniSync/
├── Assets/                     # Ícone do aplicativo (PNG e ICO)
├── Cli/                        # Modo de linha de comando (--listar, --analisar, --sincronizar, --agendado)
├── Models/                     # Modelos de dados e enums de domínio
│   ├── SyncActionType.cs       # Tipos de ação, resolução e direção do conflito
│   ├── SyncContracts.cs        # Opções/resultado da varredura, atualizações de item, balanço, lixo/
│   ├── SyncItem.cs             # Representação observável de cada arquivo analisado
│   ├── SyncProfile.cs          # Tarefa salva: origem, destino, opções e última execução
│   ├── SyncSchedule.cs         # Agendamento da tarefa (frequência, horário, modo, conflitos)
│   └── SyncProgressReport.cs   # Telemetria de progresso, velocidade (MB/s) e ETA
├── Services/                   # Regras de negócio, sem dependência da interface
│   ├── ISyncEngine.cs          # Interface do motor de sincronização
│   ├── SyncEngine.cs           # Varredura paralela, SHA-256 paralelo, cópia atômica com retentativa
│   ├── RestoreService.cs       # Restauração não destrutiva de lixo/
│   ├── PathHelper.cs           # Comparação de caminhos por SO e movimentações seguras
│   ├── FileLogService.cs       # Log diário em arquivo
│   ├── SettingsService.cs      # Tarefas salvas (JSON) e migração do formato 1.1
│   ├── ReportExporter.cs       # Exportação CSV
│   ├── SyncSafety.cs           # Critério de remoção em massa (interface e linha de comando)
│   ├── Scheduling/             # Agendador do sistema: Windows (schtasks) e Linux (systemd), verificação
│   ├── RunHistory.cs           # Histórico de execuções por tarefa (history/<id>.json)
│   ├── TaskLock.cs             # Trava por tarefa (interface x agendamento)
│   ├── AppInstaller.cs         # Instalação na pasta fixa do usuário
│   ├── ShortcutService.cs      # Atalhos na área de trabalho e no menu Iniciar
│   └── AppPaths.cs             # Pasta de dados por usuário
├── ViewModels/                 # Camada MVVM de apresentação e comandos
│   ├── MainViewModel.cs        # Tarefas, telas, análise, confirmação, execução, filtros, restauração
│   ├── MainViewModel.Schedule.cs # Cartão de agendamento, verificação e histórico
│   ├── ProfileItemViewModel.cs # Item da barra lateral de tarefas
│   ├── FolderTree.cs           # Árvore "pastas com alterações"
│   ├── ConflictDialogViewModel.cs / ConfirmSyncViewModel.cs / RestoreViewModel.cs
│   ├── UiBatchQueue.cs         # Entrega em lote de log e status à thread de UI
│   └── UiModels.cs             # Filtros, entradas de log e resumo do plano
├── Views/                      # Interfaces gráficas declarativas (Avalonia XAML)
│   ├── MainWindow.axaml(.cs)   # Janela principal
│   ├── ConflictDialogWindow / ConfirmSyncWindow / RestoreWindow / MessageDialog
│   ├── InstallWindow.cs        # Oferta de instalação com atalhos
│   ├── NotificationWindow.cs   # Aviso ao fim de uma execução agendada
│   ├── ProportionBar.cs        # Barra proporcional do resumo do plano
│   └── Converters.cs
├── Styles/                     # Paleta claro/escuro, ícones vetoriais e estilos
├── Tests/                      # Testes automatizados (xUnit)
├── docs/                       # Manuais de usuário e documentação de arquitetura
├── App.axaml(.cs)              # Configuração global da aplicação e handler de exceções UI
└── Program.cs                  # Ponto de entrada e diagnóstico de falhas (crash.log na pasta de dados)
```

---

## 📄 Licença

Distribuído sob a **Licença MIT**. Consulte o arquivo [LICENSE](LICENSE) para mais informações.

O acesso SFTP usa a biblioteca [SSH.NET](https://github.com/sshnet/SSH.NET) (licença MIT).
