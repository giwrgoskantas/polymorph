@echo off
cd /d "C:\Users\molo3\Desktop\MoloMax"
if not exist out\_soak mkdir out\_soak
py -3 python\tests\soak_test.py "C:\Users\molo3\Desktop\MoloMax\out_st\Polymorph.exe" "C:\Users\molo3\Desktop\MoloMax\out\_soak" %1 %2 > out\_soak\soak_stdout.txt 2>&1
