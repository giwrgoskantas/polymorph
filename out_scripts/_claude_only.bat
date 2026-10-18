@echo off
cd /d "C:\Users\molo3\Desktop\MoloMax"
dotnet build MoloMax\MoloMax.csproj -c Release -o out_st > out\_claude_b.log 2>&1
rmdir /s /q "C:\Users\molo3\Desktop\MoloMax\out\_only" 2>nul
set POLYMORPH_ST_ONLY=%~1
"C:\Users\molo3\Desktop\MoloMax\out_st\Polymorph.exe" --selftest "C:\Users\molo3\Desktop\MoloMax\out\_only"
echo done> "C:\Users\molo3\Desktop\MoloMax\out\_claude_only.done"
