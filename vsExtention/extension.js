const vscode = require('vscode');
const { LanguageClient, TransportKind, State } = require('vscode-languageclient/node');
const path = require('path');
const fs = require('fs');

let client;
let statusItem;
let channel;
let askedAssociation = false;
let disposed = false;
let crashRestarts = 0;
let offeredServerRestart = false;
let extContext;

function log(msg) {
    if (channel) channel.appendLine(`[${new Date().toLocaleTimeString()}] ${msg}`);
}

function resolveTool(context, configured, name) {
    const exe = name + (process.platform === 'win32' ? '.exe' : '');

    if (context) {
        const bundled = path.join(context.extensionPath, 'server', exe);
        if (fs.existsSync(bundled)) return bundled;
    }

    if (configured && configured !== name) {
        if (fs.existsSync(configured)) return configured;
        const inDir = path.join(configured, exe);
        if (fs.existsSync(inDir)) return inDir;
    }
    const installDirs = [];
    if (process.env['ProgramFiles']) installDirs.push(path.join(process.env['ProgramFiles'], 'HSharp'));
    if (process.env['LOCALAPPDATA']) installDirs.push(path.join(process.env['LOCALAPPDATA'], 'Programs', 'HSharp'));
    for (const dir of installDirs) {
        const candidate = path.join(dir, exe);
        if (fs.existsSync(candidate)) return candidate;
    }
    for (const dir of (process.env.PATH || '').split(path.delimiter)) {
        if (!dir) continue;
        const candidate = path.join(dir, exe);
        if (fs.existsSync(candidate)) return candidate;
    }
    return null;
}

function findLsp(context) {
    const cfg = vscode.workspace.getConfiguration('hsharp');
    return resolveTool(context, cfg.get('languageServerPath', 'hsharp-lsp'), 'hsharp-lsp');
}

function findCompiler(context) {
    const cfg = vscode.workspace.getConfiguration('hsharp');
    return resolveTool(context, cfg.get('compilerPath', 'hsc'), 'hsc');
}

function startClient(context) {
    const lsp = findLsp(context);
    if (!lsp) {
        statusItem.text = '$(circle-slash) H# not installed';
        statusItem.tooltip = 'hsharp-lsp not found. Click for details.';
        statusItem.show();
        log('language server binary not found');
        vscode.window.showErrorMessage(
            'H# language server not found. It ships inside this extension; if completions are missing, use "H#: Restart the language server" or set hsharp.languageServerPath.',
            'Open Settings'
        ).then(choice => {
            if (choice === 'Open Settings') vscode.commands.executeCommand('workbench.action.openSettings', 'hsharp.languageServerPath');
        });
        return;
    }

    const trace = vscode.workspace.getConfiguration('hsharp').get('trace.server', 'off');
    const serverOptions = {
        command: lsp,
        transport: TransportKind.stdio
    };

    // outline and highlighting refresh on every keystroke, so a failure there
    // would popup on every keystroke too: fail silent instead, the next
    // keystroke retries anyway. next() must be called with ALL its original
    // arguments (dropping the document crashed the client's own converter
    // synchronously, which no .catch could intercept - that was the
    // per-keystroke documentSymbol toast), and the guard must catch
    // synchronous throws too
    const guarded = (empty, ...args) => {
        const next = args[args.length - 1];
        const pass = args.slice(0, -1);
        try {
            return Promise.resolve(next(...pass)).catch(e => {
                log('request failed silently: ' + (e && e.message || e));
                return empty;
            });
        } catch (e) {
            log('request failed silently (sync): ' + (e && e.message || e));
            return Promise.resolve(empty);
        }
    };

    // no LSP failure may ever become an editor toast: catch every request
    // the client sends and degrade to the empty shape for that method
    const EMPTY_RESULTS = {
        'textDocument/completion': { isIncomplete: false, items: [] },
        'textDocument/semanticTokens/full': { data: [] },
        'textDocument/documentSymbol': [],
        'textDocument/foldingRange': [],
        'textDocument/formatting': [],
        'textDocument/codeAction': [],
        'textDocument/inlayHint': [],
        'textDocument/references': [],
        'workspace/symbol': []
    };

    const clientOptions = {
        documentSelector: [
            { scheme: 'file', language: 'hsharp' },
            { scheme: 'file', pattern: '**/*.hs' },
            { scheme: 'untitled', language: 'hsharp' }
        ],
        middleware: {
            sendRequest: async (type, param, token, next) => {
                try {
                    return await next(type, param, token);
                } catch (e) {
                    const method = typeof type === 'string' ? type : type.method;
                    log('request failed silently (' + method + '): ' + (e && e.message || e));
                    const empty = EMPTY_RESULTS[method];
                    return empty !== undefined ? empty : null;
                }
            },
            provideDocumentSymbols: (doc, token, next) => guarded([], doc, token, next),
            provideSemanticTokens: (doc, token, next) => guarded({ data: [] }, doc, token, next),
            provideWorkspaceSymbols: (query, token, next) => guarded([], query, token, next)
        },
        outputChannel: channel,
        traceOutputChannel: channel
    };

    log('starting language server: ' + lsp + ' (trace: ' + trace + ')');
    client = new LanguageClient('hsharpLsp', 'H# Language Server', serverOptions, clientOptions);

    client.onDidChangeState(e => {
        if (e.newState === State.Running) {
            crashRestarts = Math.max(0, crashRestarts - 1);
            statusItem.text = '$(check) H#';
            statusItem.tooltip = 'H# language server running: ' + lsp;
            log('server running');
        } else if (e.newState === State.Stopped && !disposed && client) {
            log('server stopped unexpectedly');
            if (crashRestarts < 3) {
                crashRestarts++;
                statusItem.text = '$(sync~spin) H# restarting';
                setTimeout(() => {
                    if (disposed || !client) return;
                    log('restart attempt ' + crashRestarts);
                    client.start();
                }, 1000 * crashRestarts);
            } else {
                statusItem.text = '$(error) H# server crashed';
                statusItem.tooltip = 'The language server keeps crashing. See the H# Language Server output channel.';
                statusItem.show();
            }
        }
    });

    context.subscriptions.push({ dispose: () => { client && client.stop(); } });
    client.start().then(async () => {
        const info = client.initializeResult && client.initializeResult.serverInfo;
        const extVer = context.extension.packageJSON.version;
        if (info) {
            log(`server ready: ${info.name} version ${info.version || 'unknown'} at ${lsp}`);
            if (info.version !== extVer) {
                log(`VERSION MISMATCH: extension ${extVer} but server reports ${info.version}. An old copy of hsharp-lsp is running - restarting in-window.`);
                statusItem.text = '$(warning) H# old server';
                statusItem.tooltip = `Server reports ${info.version} but the extension is ${extVer}. Restarting the server in this window.`;
                statusItem.show();
                if (!offeredServerRestart) {
                    offeredServerRestart = true;
                    const choice = await vscode.window.showWarningMessage(
                        `H# is running an old language server (${info.version || 'unknown'}), extension is ${extVer}. Restart it now to get the fixed one.`,
                        'Restart server now'
                    );
                    if (choice === 'Restart server now') {
                        await vscode.commands.executeCommand('hsharp.restartServer');
                    }
                }
            } else {
                statusItem.text = '$(check) H#';
                statusItem.tooltip = `H# language server ${info.version}\n${lsp}`;
            }
        }
    }).catch(e => {
        log('server failed to start: ' + (e && e.message || e));
    });
    void client.setTrace(trace === 'off' ? 'off' : trace);
    statusItem.text = '$(sync~spin) H#';
}

// try the contributed task first; when VS Code has not registered it (a
// known flake with contributed process tasks), build directly in a terminal
async function runTask(name, kind) {
    let task;
    try {
        const tasks = await vscode.tasks.fetchTasks();
        task = tasks.find(t => t.name === name);
        log(`run '${name}': fetchTasks found ${tasks.length} task(s)${task ? ', match found' : ', no match'}`);
    } catch (e) {
        log(`fetchTasks failed: ${e}`);
    }
    if (task) {
        await vscode.tasks.executeTask(task);
        return;
    }
    buildDirect(kind);
}

// build (and optionally run) straight through a task with a ShellExecution,
// so every shell gets correct quoting; no contributed-task registration
// needed
function buildDirect(kind) {
    const doc = vscode.window.activeTextEditor && vscode.window.activeTextEditor.document;
    if (!doc) {
        vscode.window.showInformationMessage('Open an H# file first.');
        return Promise.resolve(false);
    }
    const hsc = findCompiler(extContext);
    if (!hsc) {
        vscode.window.showErrorMessage('hsc was not found. Install H# or set hsharp.compilerPath.');
        return Promise.resolve(false);
    }
    const exe = path.join(path.dirname(doc.fileName), path.basename(doc.fileName, path.extname(doc.fileName)) + (process.platform === 'win32' ? '.exe' : ''));

    const runOnce = (command, args, name) => new Promise(resolve => {
        const exec = new vscode.ShellExecution(command, args);
        const task = new vscode.Task({ type: 'hsharp-direct' }, vscode.TaskScope.Workspace, 'H#: ' + name, 'hsharp', exec);
        const d = vscode.tasks.onDidEndTaskProcess(e => {
            if (e.execution.task !== task) return;
            d.dispose();
            resolve(e.exitCode === 0);
        });
        vscode.tasks.executeTask(task).catch(() => { d.dispose(); resolve(false); });
    });

    if (kind === 'check') return runOnce(hsc, [doc.fileName, '--check'], 'check');
    return runOnce(hsc, [doc.fileName, '-o', exe], 'build').then(ok =>
        ok && kind === 'run' ? runOnce(exe, [], 'run') : ok);
}

function watchTasks(context) {
    let started = 0;
    context.subscriptions.push(
        vscode.tasks.onDidStartTask(e => {
            if (!e.execution.task.name || !e.execution.task.name.startsWith('H#:')) return;
            started = Date.now();
            statusItem.text = '$(sync~spin) H# working...';
            statusItem.tooltip = e.execution.task.name;
            statusItem.show();
        }),
        vscode.tasks.onDidEndTask(e => {
            if (!e.execution.task.name || !e.execution.task.name.startsWith('H#:')) return;
            const secs = ((Date.now() - started) / 1000).toFixed(1);
            const ok = e.exitCode === 0;
            statusItem.text = ok ? `$(check) H# ${secs}s` : '$(error) H# failed';
            statusItem.tooltip = `${e.execution.task.name} ${ok ? 'succeeded' : 'failed'} (${secs}s). Errors are in the Problems panel.`;
            statusItem.show();
            setTimeout(() => { statusItem.hide(); }, 6000);
        })
    );
}

function activate(context) {
    extContext = context;
    channel = vscode.window.createOutputChannel('H# Language Server');
    context.subscriptions.push(channel);
    log('extension ' + context.extension.packageJSON.version + ' activating');

    statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
    statusItem.command = 'hsharp.showSetup';
    context.subscriptions.push(statusItem);
    statusItem.text = '$(sync~spin) H#';
    statusItem.show();

    startClient(context);
    watchTasks(context);

    context.subscriptions.push(
        vscode.commands.registerCommand('hsharp.run', async () => {
            const doc = vscode.window.activeTextEditor && vscode.window.activeTextEditor.document;
            if (doc) await doc.save();
            await runTask('H#: Build and run', 'run');
        }),
        vscode.commands.registerCommand('hsharp.build', async () => {
            const doc = vscode.window.activeTextEditor && vscode.window.activeTextEditor.document;
            if (doc) await doc.save();
            await runTask('H#: Build program', 'build');
        }),
        vscode.commands.registerCommand('hsharp.check', async () => {
            const doc = vscode.window.activeTextEditor && vscode.window.activeTextEditor.document;
            if (doc) await doc.save();
            await runTask('H#: Check current file', 'check');
        }),
        vscode.commands.registerCommand('hsharp.showSetup', () => {
            const lsp = findLsp(context);
            const hsc = findCompiler(context);
            vscode.window.showInformationMessage(
                `H# setup\nCompiler (hsc): ${hsc || 'not found'}\nLanguage server: ${lsp || 'not found'}\n\nCheck/Build/Run live in the Terminal > Run Task menu. Set hsharp.compilerPath or hsharp.languageServerPath if the tools were not found.`,
                'Open Settings', 'Show Server Log'
            ).then(choice => {
                if (choice === 'Open Settings') vscode.commands.executeCommand('workbench.action.openSettings', 'hsharp.');
                if (choice === 'Show Server Log') channel.show(true);
            });
        }),

        vscode.commands.registerCommand('hsharp.restartServer', async () => {
            disposed = false;
            if (client) {
                const old = client;
                client = undefined;
                await old.stop();
            }
            startClient(context);
        }),

        vscode.commands.registerCommand('hsharp.associateHs', () => {
            const config = vscode.workspace.getConfiguration('files');
            const assoc = config.get('associations', {});
            assoc['*.hs'] = 'hsharp';
            config.update('associations', assoc, vscode.ConfigurationTarget.Global);
        }),

        vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration('hsharp')) {
                vscode.commands.executeCommand('hsharp.restartServer');
            }
        }),

        vscode.workspace.onDidOpenTextDocument(doc => {
            if (doc.languageId === 'haskell' && doc.fileName.endsWith('.hs')) {
                promptHaskellAssociation();
            }
        })
    );

    for (const doc of vscode.workspace.textDocuments) {
        if (doc.languageId === 'haskell' && doc.fileName.endsWith('.hs')) {
            promptHaskellAssociation();
        }
    }
}

function promptHaskellAssociation() {
    if (askedAssociation) return;
    askedAssociation = true;

    vscode.window.showInformationMessage(
        'This .hs file is open as Haskell. Associate .hs with H# so the H# tasks and tooling handle it instead?',
        'Associate with H# (workspace)',
        'Associate globally',
        'Keep Haskell'
    ).then(choice => {
        if (choice === 'Keep Haskell') return;
        const config = vscode.workspace.getConfiguration('files');
        const assoc = config.get('associations', {});
        assoc['*.hs'] = 'hsharp';
        const scope = vscode.workspace.workspaceFolders && choice === 'Associate with H# (workspace)'
            ? vscode.ConfigurationTarget.Workspace
            : vscode.ConfigurationTarget.Global;
        config.update('associations', assoc, scope);
    });
}

function deactivate() {
    disposed = true;
    if (client) return client.stop();
}

module.exports = { activate, deactivate };
