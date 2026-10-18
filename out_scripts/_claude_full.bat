@echo off
cd /d "C:\Users\molo3\Desktop\MoloMax"
set R=C:\Users\molo3\Desktop\MoloMax\out\_full
rmdir /s /q "%R%" 2>nul
mkdir "%R%"
echo build> "%R%\stage.txt"
dotnet build MoloMax\MoloMax.csproj -c Release -o out_st > "%R%\build_app.log" 2>&1
echo code=%errorlevel%> "%R%\build_app.code"
dotnet build MoloMax.Core.Tests\MoloMax.Core.Tests.csproj -c Release > "%R%\build_tests.log" 2>&1
echo unit> "%R%\stage.txt"
dotnet MoloMax.Core.Tests\bin\Release\net8.0\MoloMax.Core.Tests.dll > "%R%\unit.log" 2>&1
echo code=%errorlevel%> "%R%\unit.code"
echo game_legacy> "%R%\stage.txt"
dotnet MoloMax.Core.Tests\bin\Release\net8.0\MoloMax.Core.Tests.dll game "C:\Program Files\Rockstar Games\Grand Theft Auto V Legacy" "C:\Users\molo3\Desktop\MoloMax\out\_gt_legacy" > "%R%\game_legacy.log" 2>&1
echo code=%errorlevel%> "%R%\game_legacy.code"
echo game_enhanced> "%R%\stage.txt"
dotnet MoloMax.Core.Tests\bin\Release\net8.0\MoloMax.Core.Tests.dll game "C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V Enhanced" "C:\Users\molo3\Desktop\MoloMax\out\_gt_enhanced" > "%R%\game_enhanced.log" 2>&1
echo code=%errorlevel%> "%R%\game_enhanced.code"
echo selftest> "%R%\stage.txt"
rmdir /s /q "C:\Users\molo3\Desktop\MoloMax\out\_selftest" 2>nul
"C:\Users\molo3\Desktop\MoloMax\out_st\Polymorph.exe" --selftest "C:\Users\molo3\Desktop\MoloMax\out\_selftest" "C:\Program Files\Rockstar Games\Grand Theft Auto V Legacy" "C:\Users\molo3\Desktop\MoloMax\out\_gt_legacy\extract\prop_bench_01a.yft" "C:\Users\molo3\Desktop\MoloMax\out\_gt_legacy\mlo\v_int_1.ytyp"
echo code=%errorlevel%> "%R%\selftest.code"
echo render> "%R%\stage.txt"
rmdir /s /q "C:\Users\molo3\Desktop\MoloMax\out\_render_gims" 2>nul
"C:\Users\molo3\Desktop\MoloMax\out_st\Polymorph.exe" --selftest "C:\Users\molo3\Desktop\MoloMax\out\_render_gims" --render "C:\Users\molo3\Desktop\MoloMax\scenes\molo_bench_leather_worn\molo_bench_leather_worn.mmx"
echo realapp> "%R%\stage.txt"
py -3 python\tests\real_app_test.py "C:\Users\molo3\Desktop\MoloMax\out_st\Polymorph.exe" "%R%\realapp" > "%R%\realapp.log" 2>&1
echo code=%errorlevel%> "%R%\realapp.code"
echo publish> "%R%\stage.txt"
dotnet publish MoloMax\MoloMax.csproj -c Release -r win-x64 --self-contained false -o out_pub > "%R%\publish.log" 2>&1
echo code=%errorlevel%> "%R%\publish.code"
echo done> "%R%\stage.txt"
echo done> "C:\Users\molo3\Desktop\MoloMax\out\_claude_full.done"
