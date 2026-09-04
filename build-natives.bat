dotnet publish .\src\HSharp\compiler\compiler.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:UseAppHost=true
dotnet publish .\src\HSharp\lsp\HSharp.Lsp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:UseAppHost=true
pause