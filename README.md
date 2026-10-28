# 🔄 OmniSync — Sincronizador de Arquivos Multiplataforma

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
3. **Arquivos Órfãos (`destino/lixo/`)**: Arquivos que não existem mais na Origem, mas permanecem no Destino, **nunca são excluídos diretamente** — são movidos para a pasta `<Destino>/lixo/<caminho_relativo>` e podem ser restaurados com 1 clique.
4. **Arquivos Conflitantes (`destino/old/`)**: Quando um arquivo existe em ambos os diretórios com o mesmo nome mas conteúdo diferente, o aplicativo exibe um **modal comparativo lado a lado** permitindo escolher entre **Ignorar** ou **Substituir** (com opção *"Aplicar para todos"*). Ao substituir, a versão antiga do destino é preservada em `<Destino>/old/<caminho_relativo>`.
5. **Subpasta Automática por Nome da Origem**: Ao selecionar uma pasta de origem (ex.: `E:\Projetos`) e apontar para a raiz de outro drive (ex.: `G:\`), o OmniSync direciona automaticamente a sincronização para `G:\Projetos`, protegendo as demais pastas do disco de destino.

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

# Autoteste de ponta a ponta da interface gráfica
dotnet run --project OmniSync.csproj -- --self-test
```

### 3. Gerar executável único (Standalone Single-File)

**No Windows (`win-x64`):**
```powershell
dotnet publish OmniSync.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```
O executável final estará em `publish/win-x64/OmniSync.exe` (também acionável pelo atalho `Iniciar OmniSync.bat`).

**No Linux (`linux-x64`):**
```bash
dotnet publish OmniSync.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish/linux-x64
chmod +x publish/linux-x64/OmniSync
```

---

## 🗂️ Estrutura do Projeto

```text
OmniSync/
├── Assets/                     # Ícones e recursos visuais embutidos
├── Models/                     # Modelos de dados e enums de domínio
│   ├── SyncActionType.cs       # Tipos de ação (Identical, New, OrphanTrash, Conflict) e resolução
│   ├── SyncItem.cs             # Representação observável de cada arquivo analisado
│   └── SyncProgressReport.cs   # Telemetria de progresso, velocidade (MB/s) e ETA
├── Services/                   # Camada de serviços e regras de negócio
│   ├── ISyncEngine.cs          # Interface do motor de sincronização
│   └── SyncEngine.cs           # Implementação assíncrona resiliente (I/O, SHA-256, Retry)
├── ViewModels/                 # Camada MVVM de apresentação e comandos
│   ├── ViewModelBase.cs        # Classe base ObservableObject
│   ├── MainViewModel.cs        # Orquestração da tela principal, filtros, logs e restauração
│   └── ConflictDialogViewModel.cs # Estado e decisão do modal de conflito
├── Views/                      # Interfaces gráficas declarativas (Avalonia XAML)
│   ├── MainWindow.axaml(.cs)   # Janela principal e integração nativa com StorageProvider
│   └── ConflictDialogWindow.axaml(.cs) # Modal comparativo lado a lado
├── Tests/                      # Projeto de testes automatizados (xUnit)
│   └── SyncEngineTests.cs      # Testes de integração das regras de sincronização
├── docs/                       # Manuais de usuário e documentação de arquitetura
├── App.axaml(.cs)              # Configuração global da aplicação e handler de exceções UI
└── Program.cs                  # Ponto de entrada e diagnóstico de falhas (crash.log)
```

---

## 📄 Licença

Distribuído sob a **Licença MIT**. Consulte o arquivo [LICENSE](LICENSE) para mais informações.
