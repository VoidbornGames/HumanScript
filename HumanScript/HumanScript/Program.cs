using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

class HumanScript
{
    // Global state for the compiler
    static Dictionary<string, string> variables = new Dictionary<string, string>();
    static Dictionary<string, string> variableTypes = new Dictionary<string, string>();
    static Dictionary<string, List<string>> functionBodies = new Dictionary<string, List<string>>();
    static List<string> bssSection = new List<string>();
    static List<string> dataSection = new List<string>();
    static List<string> codeSection = new List<string>();
    static Stack<int> ifCountStack = new Stack<int>();
    static Stack<int> loopCountStack = new Stack<int>();
    static int strCount = 0;
    static int ifCount = 0;
    static int loopCount = 0;
    static int uniqueId = 0;

    // --- FIX: List to hold string initializations ---
    static List<(string varLabel, string initLabel)> stringInits = new List<(string, string)>();

    static void Main(string[] mainArgs)
    {
        string inputFile = "script.eng";
        string asmFile = "temp.asm";
        string exeFile = "output.exe";

        if (mainArgs.Length > 0)
            if (string.IsNullOrEmpty(mainArgs[0]) || !string.IsNullOrEmpty(mainArgs[0]) && !mainArgs[0].EndsWith(".eng"))
            {
                Console.WriteLine($"File {inputFile} is not a valid '.eng' HumanScript file!");
                return;
            }
            else
            {
                inputFile = mainArgs[0];
            }

        if (!File.Exists(inputFile))
        {
            Console.WriteLine($"File {inputFile} not found.");
            return;
        }

        var lines = File.ReadAllLines(inputFile);

        // --- PASS 1: Parse all global variables and function definitions ---
        ParseFunctionsAndGlobals(lines);

        // --- PASS 2: Use a simple flag to separate main code from functions ---
        var mainProgramLines = new List<string>();
        bool inMainCode = true;
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("define function named"))
            {
                inMainCode = false;
                continue;
            }
            if (trimmed == "]")
            {
                inMainCode = true;
                continue;
            }

            if (inMainCode)
            {
                mainProgramLines.Add(line);
            }
        }

        // --- Compile the main program and all functions ---
        codeSection.Add("global main");
        codeSection.Add("extern printf");
        codeSection.Add("extern scanf");
        codeSection.Add("extern exit");
        codeSection.Add("extern Sleep");
        codeSection.Add("extern strcpy");
        // --- CHANGE: Use Windows API for console input ---
        codeSection.Add("extern GetStdHandle");
        codeSection.Add("extern ReadConsoleA");
        codeSection.Add("extern CreateProcessA");
        codeSection.Add("extern CreateDirectoryA");
        codeSection.Add("extern CreateFileA");
        codeSection.Add("extern WriteFile");
        codeSection.Add("extern CloseHandle");
        codeSection.Add("extern sprintf");
        codeSection.Add("extern ReadFile");
        codeSection.Add("extern DeleteFileA");
        codeSection.Add("extern MoveFileA");
        codeSection.Add("extern strcat");
        codeSection.Add("section .text");
        codeSection.Add("main:");

        // --- CHANGE: Get the handle for the console input at the start of the program ---
        codeSection.Add("push -10");                 // STD_INPUT_HANDLE = -10
        codeSection.Add("call GetStdHandle");
        codeSection.Add("mov [hConsoleInput], eax");  // Store the handle

        // --- NEW: Initialize the STARTUPINFO structure for CreateProcessA ---
        // The first member (cb) must be set to the size of the structure.
        codeSection.Add("mov dword [startup_info], 68"); // sizeof(STARTUPINFOA) is 68

        // Initialize all string variables at the start of main
        foreach (var (varLabel, initLabel) in stringInits)
        {
            codeSection.Add($"push {initLabel}"); // Source
            codeSection.Add($"push {varLabel}");  // Destination
            codeSection.Add("call strcpy");
            codeSection.Add("add esp, 8");
        }

        CompileCodeBlock(mainProgramLines, 0);

        // --- FIX: Explicitly exit after main code is done ---
        // This prevents execution from "falling through" into function definitions.
        codeSection.Add("push 0");
        codeSection.Add("call exit");

        foreach (var func in functionBodies)
        {
            codeSection.Add($"{func.Key}:");
            var funcBodyLines = func.Value.Where(l => l.Trim() != "[" && l.Trim() != "]").ToList();
            CompileCodeBlock(funcBodyLines, 4);
            codeSection.Add("ret");
        }

        // --- Finalize and Write Assembly ---
        // The old "push 0; call exit" is now removed from here
        using (var sw = new StreamWriter(asmFile))
        {
            sw.WriteLine("section .data");
            foreach (var d in dataSection) sw.WriteLine(d);
            sw.WriteLine();
            sw.WriteLine("section .bss");
            foreach (var b in bssSection) sw.WriteLine(b);
            sw.WriteLine();
            sw.WriteLine("section .text");
            foreach (var c in codeSection) sw.WriteLine(c);
        }

        // --- Assemble & Link ---
        Console.WriteLine("Assembling...");
        var pAsm = new Process();
        pAsm.StartInfo.FileName = @"NASM\nasm.exe";
        pAsm.StartInfo.Arguments = $"-f win32 {asmFile} -o temp.obj";
        pAsm.StartInfo.UseShellExecute = false;
        pAsm.Start();
        pAsm.WaitForExit();

        Console.WriteLine("Linking...");
        var pLink = new Process();
        pLink.StartInfo.FileName = @"Golink\GoLink.exe";
        pLink.StartInfo.Arguments = $"temp.obj msvcrt.dll kernel32.dll /entry main /console /mix {exeFile}";
        pLink.StartInfo.UseShellExecute = false;
        pLink.Start();
        pLink.WaitForExit();

        if (File.Exists("temp.exe"))
        {
            File.Copy("temp.exe", exeFile, true);
            File.Delete("temp.exe");
        }

        Console.WriteLine($"Compilation finished: {exeFile}");
    }

    // --- Core compilation logic for a block of code ---
    static void CompileCodeBlock(List<string> lines, int startingIndent)
    {
        // A single stack to manage the end labels of all nested structures (if, repeat)
        var blockEndStack = new Stack<string>();
        var endIfStack = new Stack<string>();
        var elseStack = new Stack<string>();
        var loopLabelStack = new Stack<string>();
        int prevIndent = startingIndent;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            int commentIndex = line.IndexOf('#');
            if (commentIndex != -1)
            {
                line = line.Substring(0, commentIndex);
            }

            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("#")) continue;

            if (line == "[" || line == "]") continue;

            int indent = line.Length - line.TrimStart().Length;
            string trimmed = line.Trim();

            // Handle dedent (closing blocks)
            while (indent < prevIndent)
            {
                if (endIfStack.Count > 0)
                {
                    string endIfLabel = endIfStack.Pop();
                    // If there's an 'else' part, define its label first
                    if (elseStack.Count > 0)
                    {
                        codeSection.Add($"{elseStack.Pop()}:");
                    }
                    // Then define the final end_if label
                    codeSection.Add($"{endIfLabel}:");
                }
                prevIndent -= 4;
            }
            prevIndent = indent;

            // --- FIX: Add syntax for 'set <var> to <number>' ---
            var mSetNum = Regex.Match(trimmed, @"^set (\w+) to (\d+)\.$");
            if (mSetNum.Success)
            {
                string varName = mSetNum.Groups[1].Value;
                int value = int.Parse(mSetNum.Groups[2].Value);
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }
                codeSection.Add($"mov dword [{variables[varName]}], {value}");
                continue;
            }

            var mStringConcat = Regex.Match(trimmed, @"^set (\w+) to (\w+) combined with (\w+)\.$");
            if (mStringConcat.Success)
            {
                string destVar = mStringConcat.Groups[1].Value;
                string srcVar1 = mStringConcat.Groups[2].Value;
                string op = "combined with";
                string srcVar2 = mStringConcat.Groups[3].Value;

                if (!variables.ContainsKey(destVar) || !variables.ContainsKey(srcVar1) || !variables.ContainsKey(srcVar2))
                {
                    Console.WriteLine($"Error in string concatenation: one or more variables not defined.");
                    return;
                }

                // Check if both variables are strings
                bool var1IsString = variableTypes.ContainsKey(srcVar1) && variableTypes[srcVar1] == "string";
                bool var2IsString = variableTypes.ContainsKey(srcVar2) && variableTypes[srcVar2] == "string";

                if (!var1IsString || !var2IsString)
                {
                    Console.WriteLine($"Error: String concatenation requires both variables to be strings.");
                    Console.WriteLine($"Variables: {srcVar1} ({(var1IsString ? "string" : "number")}), {srcVar2} ({(var2IsString ? "string" : "number")})");
                    return;
                }

                // Generate unique labels
                int cmdId = uniqueId++;
                string tempBuffer = $"concat_temp_{cmdId}";

                // Reserve space for concatenated string (assume max 512 characters total)
                bssSection.Add($"{tempBuffer} resb 512");

                // First, copy the first string to temp buffer
                codeSection.Add($"push {variables[srcVar1]}");     // Source
                codeSection.Add($"push {tempBuffer}");             // Destination
                codeSection.Add("call strcpy");
                codeSection.Add("add esp, 8");

                // Then, concatenate the second string to temp buffer
                codeSection.Add($"push {variables[srcVar2]}");     // Source to append
                codeSection.Add($"push {tempBuffer}");             // Destination
                codeSection.Add("call strcat");                    // You'll need to add extern strcat
                codeSection.Add("add esp, 8");

                // Finally, copy result back to destination variable
                codeSection.Add($"push {tempBuffer}");             // Source
                codeSection.Add($"push {variables[destVar]}");     // Destination
                codeSection.Add("call strcpy");
                codeSection.Add("add esp, 8");

                // Make sure destination is marked as string type
                variableTypes[destVar] = "string";

                continue;
            }

            // Pre-calculate all print data to stabilize the data section
            var mPrint = Regex.Match(trimmed, @"^print (\w+)\.$");
            if (mPrint.Success)
            {
                string varName = mPrint.Groups[1].Value;
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }

                string sLabel = $"str_{strCount++}";
                if (variableTypes.ContainsKey(varName) && variableTypes[varName] == "string")
                {
                    dataSection.Add($"{sLabel} db \"%s\",10,0");
                }
                else
                {
                    dataSection.Add($"{sLabel} db \"%d\",10,0");
                }
                if (variableTypes.ContainsKey(varName) && variableTypes[varName] == "string")
                {
                    codeSection.Add($"push {variables[varName]}");
                }
                else
                {
                    codeSection.Add($"push dword [{variables[varName]}]");
                }
                codeSection.Add($"push {sLabel}");
                codeSection.Add("call printf");
                codeSection.Add("add esp, 8");
                continue;
            }

            var mPrintStr = Regex.Match(trimmed, @"^print ""(.*)""\.$");
            if (mPrintStr.Success)
            {
                string text = mPrintStr.Groups[1].Value;
                string sLabel = $"str_{strCount++}";
                dataSection.Add($"{sLabel} db \"{text}\",10,0");
                codeSection.Add($"push {sLabel}");
                codeSection.Add("call printf");
                codeSection.Add("add esp, 4");
                continue;
            }

            var mVarOpNum = Regex.Match(trimmed, @"^(add|subtract|multiply|divide)\s+(\w+)\s+(to|from|by)\s+(\d+)\.$");
            if (mVarOpNum.Success)
            {
                string op = mVarOpNum.Groups[1].Value;
                string varName = mVarOpNum.Groups[2].Value;
                int value = int.Parse(mVarOpNum.Groups[4].Value);
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }
                string label = variables[varName];
                codeSection.Add($"mov eax, [{label}]");
                switch (op)
                {
                    case "add": codeSection.Add($"add eax, {value}"); break;
                    case "subtract": codeSection.Add($"sub eax, {value}"); break;
                    case "multiply": codeSection.Add($"imul eax, {value}"); break;
                    case "divide":
                        codeSection.Add($"xor edx, edx");
                        codeSection.Add($"mov ecx, {value}");
                        codeSection.Add($"idiv ecx");
                        break;
                }
                codeSection.Add($"mov [{label}], eax");
                continue;
            }

            var mRunFunc = Regex.Match(trimmed, @"^run function (\w+)\.$");
            if (mRunFunc.Success)
            {
                string funcName = mRunFunc.Groups[1].Value;
                if (!functionBodies.ContainsKey(funcName))
                {
                    Console.WriteLine($"Error: function '{funcName}' is not defined.");
                    return;
                }
                codeSection.Add($"call {funcName}");
                continue;
            }

            var mSet = Regex.Match(trimmed, @"^define (\w+) as (.+)\.$");
            if (mSet.Success) continue;

            var mWait = Regex.Match(trimmed, @"^wait for (\d+) seconds\.$");
            if (mWait.Success)
            {
                int milliseconds = int.Parse(mWait.Groups[1].Value) * 1000;
                codeSection.Add($"push {milliseconds}");
                codeSection.Add("call Sleep");
                codeSection.Add("add esp, 4");
                continue;
            }

            // Handle math operations with numbers: set var to num op num
            var mMathWithNumbers = Regex.Match(trimmed, @"^set (\w+) to (\d+) (times|plus|minus|divided by) (\d+)\.$");
            if (mMathWithNumbers.Success)
            {
                string destVar = mMathWithNumbers.Groups[1].Value;
                int num1 = int.Parse(mMathWithNumbers.Groups[2].Value);
                string op = mMathWithNumbers.Groups[3].Value;
                int num2 = int.Parse(mMathWithNumbers.Groups[4].Value);

                if (!variables.ContainsKey(destVar))
                {
                    Console.WriteLine($"Error: destination variable '{destVar}' not defined.");
                    return;
                }

                // Check if destination is a string (can't store math result in string)
                bool destIsString = variableTypes.ContainsKey(destVar) && variableTypes[destVar] == "string";
                if (destIsString)
                {
                    Console.WriteLine($"Error: Cannot store math result in string variable '{destVar}'.");
                    return;
                }

                codeSection.Add($"mov eax, {num1}");
                switch (op)
                {
                    case "times":
                        codeSection.Add($"mov ebx, {num2}");
                        codeSection.Add("imul ebx");
                        break;
                    case "plus":
                        codeSection.Add($"add eax, {num2}");
                        break;
                    case "minus":
                        codeSection.Add($"sub eax, {num2}");
                        break;
                    case "divided by":
                        codeSection.Add("xor edx, edx");
                        codeSection.Add($"mov ebx, {num2}");
                        codeSection.Add("idiv ebx");
                        break;
                }
                codeSection.Add($"mov [{variables[destVar]}], eax");
                continue;
            }

            // Handle math operations with variables: set var to var op var
            var mVarMath = Regex.Match(trimmed, @"^set (\w+) to (\w+) (times|plus|minus|divided by) (\w+)\.$");
            if (mVarMath.Success)
            {
                string destVar = mVarMath.Groups[1].Value;
                string srcVar1 = mVarMath.Groups[2].Value;
                string op = mVarMath.Groups[3].Value;
                string srcVar2 = mVarMath.Groups[4].Value;

                if (!variables.ContainsKey(destVar) || !variables.ContainsKey(srcVar1) || !variables.ContainsKey(srcVar2))
                {
                    Console.WriteLine($"Error in variable-to-variable math: one or more variables not defined.");
                    return;
                }

                // Type checking
                bool var1IsString = variableTypes.ContainsKey(srcVar1) && variableTypes[srcVar1] == "string";
                bool var2IsString = variableTypes.ContainsKey(srcVar2) && variableTypes[srcVar2] == "string";
                bool destIsString = variableTypes.ContainsKey(destVar) && variableTypes[destVar] == "string";

                if (var1IsString || var2IsString)
                {
                    Console.WriteLine($"Error: Cannot perform math operations with string variables.");
                    Console.WriteLine($"Variables: {srcVar1} ({(var1IsString ? "string" : "number")}), {srcVar2} ({(var2IsString ? "string" : "number")})");
                    return;
                }

                if (destIsString)
                {
                    Console.WriteLine($"Error: Cannot store math result in string variable '{destVar}'.");
                    return;
                }

                codeSection.Add($"mov eax, [{variables[srcVar1]}]");
                switch (op)
                {
                    case "times": codeSection.Add($"imul dword [{variables[srcVar2]}]"); break;
                    case "plus": codeSection.Add($"add eax, [{variables[srcVar2]}]"); break;
                    case "minus": codeSection.Add($"sub eax, [{variables[srcVar2]}]"); break;
                    case "divided by":
                        codeSection.Add($"xor edx, edx");
                        codeSection.Add($"idiv dword [{variables[srcVar2]}]");
                        break;
                }
                codeSection.Add($"mov [{variables[destVar]}], eax");
                continue;
            }

            // Handle mixed operations: set var to var op num or set var to num op var
            var mMixedMath = Regex.Match(trimmed, @"^set (\w+) to (\w+|\d+) (times|plus|minus|divided by) (\w+|\d+)\.$");
            if (mMixedMath.Success)
            {
                string destVar = mMixedMath.Groups[1].Value;
                string operand1 = mMixedMath.Groups[2].Value;
                string op = mMixedMath.Groups[3].Value;
                string operand2 = mMixedMath.Groups[4].Value;

                if (!variables.ContainsKey(destVar))
                {
                    Console.WriteLine($"Error: destination variable '{destVar}' not defined.");
                    return;
                }

                bool destIsString = variableTypes.ContainsKey(destVar) && variableTypes[destVar] == "string";
                if (destIsString)
                {
                    Console.WriteLine($"Error: Cannot store math result in string variable '{destVar}'.");
                    return;
                }

                // Check if operands are numbers or variables
                bool op1IsNumber = int.TryParse(operand1, out int num1);
                bool op2IsNumber = int.TryParse(operand2, out int num2);

                // Load first operand
                if (op1IsNumber)
                {
                    codeSection.Add($"mov eax, {num1}");
                }
                else
                {
                    if (!variables.ContainsKey(operand1))
                    {
                        Console.WriteLine($"Error: variable '{operand1}' not defined.");
                        return;
                    }
                    bool op1IsString = variableTypes.ContainsKey(operand1) && variableTypes[operand1] == "string";
                    if (op1IsString)
                    {
                        Console.WriteLine($"Error: Cannot perform math operations with string variable '{operand1}'.");
                        return;
                    }
                    codeSection.Add($"mov eax, [{variables[operand1]}]");
                }

                // Perform operation with second operand
                switch (op)
                {
                    case "times":
                        if (op2IsNumber)
                        {
                            codeSection.Add($"mov ebx, {num2}");
                            codeSection.Add("imul ebx");
                        }
                        else
                        {
                            if (!variables.ContainsKey(operand2))
                            {
                                Console.WriteLine($"Error: variable '{operand2}' not defined.");
                                return;
                            }
                            bool op2IsString = variableTypes.ContainsKey(operand2) && variableTypes[operand2] == "string";
                            if (op2IsString)
                            {
                                Console.WriteLine($"Error: Cannot perform math operations with string variable '{operand2}'.");
                                return;
                            }
                            codeSection.Add($"imul dword [{variables[operand2]}]");
                        }
                        break;
                    case "plus":
                        if (op2IsNumber)
                        {
                            codeSection.Add($"add eax, {num2}");
                        }
                        else
                        {
                            if (!variables.ContainsKey(operand2))
                            {
                                Console.WriteLine($"Error: variable '{operand2}' not defined.");
                                return;
                            }
                            bool op2IsString = variableTypes.ContainsKey(operand2) && variableTypes[operand2] == "string";
                            if (op2IsString)
                            {
                                Console.WriteLine($"Error: Cannot perform math operations with string variable '{operand2}'.");
                                return;
                            }
                            codeSection.Add($"add eax, [{variables[operand2]}]");
                        }
                        break;
                    case "minus":
                        if (op2IsNumber)
                        {
                            codeSection.Add($"sub eax, {num2}");
                        }
                        else
                        {
                            if (!variables.ContainsKey(operand2))
                            {
                                Console.WriteLine($"Error: variable '{operand2}' not defined.");
                                return;
                            }
                            bool op2IsString = variableTypes.ContainsKey(operand2) && variableTypes[operand2] == "string";
                            if (op2IsString)
                            {
                                Console.WriteLine($"Error: Cannot perform math operations with string variable '{operand2}'.");
                                return;
                            }
                            codeSection.Add($"sub eax, [{variables[operand2]}]");
                        }
                        break;
                    case "divided by":
                        codeSection.Add("xor edx, edx");
                        if (op2IsNumber)
                        {
                            codeSection.Add($"mov ebx, {num2}");
                            codeSection.Add("idiv ebx");
                        }
                        else
                        {
                            if (!variables.ContainsKey(operand2))
                            {
                                Console.WriteLine($"Error: variable '{operand2}' not defined.");
                                return;
                            }
                            bool op2IsString = variableTypes.ContainsKey(operand2) && variableTypes[operand2] == "string";
                            if (op2IsString)
                            {
                                Console.WriteLine($"Error: Cannot perform math operations with string variable '{operand2}'.");
                                return;
                            }
                            codeSection.Add($"idiv dword [{variables[operand2]}]");
                        }
                        break;
                }
                codeSection.Add($"mov [{variables[destVar]}], eax");
                continue;
            }

            var mSetBool = Regex.Match(trimmed, @"^set (\w+) to (true|false)\.$");
            if (mSetBool.Success)
            {
                string varName = mSetBool.Groups[1].Value;
                int value = (mSetBool.Groups[2].Value == "true") ? 1 : 0;
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }
                codeSection.Add($"mov dword [{variables[varName]}], {value}");
                continue;
            }

            var mMath = Regex.Match(trimmed, @"^(add|subtract|multiply|divide) (\d+) (to|from|by) (\w+)\.$");
            if (mMath.Success)
            {
                string op = mMath.Groups[1].Value;
                string value = mMath.Groups[2].Value;
                string varName = mMath.Groups[4].Value;
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }
                string label = variables[varName];
                codeSection.Add($"mov eax, [{label}]");
                switch (op)
                {
                    case "add": codeSection.Add($"add eax, {value}"); break;
                    case "subtract": codeSection.Add($"sub eax, {value}"); break;
                    case "multiply": codeSection.Add($"imul eax, {value}"); break;
                    case "divide": // This handles 'divide <num> by <var>'
                        codeSection.Add($"xor edx, edx");
                        codeSection.Add($"mov ecx, [{label}]");
                        codeSection.Add($"mov eax, {value}");
                        codeSection.Add($"idiv ecx");
                        codeSection.Add($"mov [{label}], eax");
                        break;
                }
                if (op != "divide") codeSection.Add($"mov [{label}], eax");
                continue;
            }

            var mDivVarByNum = Regex.Match(trimmed, @"^divide (\w+) by (\d+)\.$");
            if (mDivVarByNum.Success)
            {
                string varName = mDivVarByNum.Groups[1].Value;
                int value = int.Parse(mDivVarByNum.Groups[2].Value);
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }

                string label = variables[varName];
                codeSection.Add($"mov eax, [{label}]");
                codeSection.Add($"xor edx, edx"); // Clear edx for 32-bit division
                codeSection.Add($"mov ecx, {value}");
                codeSection.Add($"idiv ecx"); // Divide eax:edx by ecx
                codeSection.Add($"mov [{label}], eax");
                continue;
            }

            // --- CORRECTED IF/ELSE IF/ELSE LOGIC ---
            var mIf = Regex.Match(trimmed, @"^if (.+):$");
            if (mIf.Success)
            {
                string condition = mIf.Groups[1].Value;
                string endIfLabel = $"end_if_{uniqueId++}";
                string elseLabel = $"else_{uniqueId++}";

                endIfStack.Push(endIfLabel);
                elseStack.Push(elseLabel); // This label is PENDING DEFINITION

                // If condition is FALSE, jump to 'else' part.
                if (!CompileCondition(codeSection, variables, condition, elseLabel, -1)) return;
                continue;
            }
            var mElseIf = Regex.Match(trimmed, @"^else if (.+):$");
            if (mElseIf.Success)
            {
                if (endIfStack.Count == 0) { Console.WriteLine($"Error: 'else if' without 'if'."); return; }
                string condition = mElseIf.Groups[1].Value;
                string endIfLabel = endIfStack.Peek();

                // The previous block (if or else-if) is finished. Jump to end of whole chain.
                codeSection.Add($"jmp {endIfLabel}");

                // Define the label that the previous block was jumping to.
                codeSection.Add($"{elseStack.Pop()}:");

                // Now, start the new else-if block.
                string newElseLabel = $"else_{uniqueId++}";
                elseStack.Push(newElseLabel);

                if (!CompileCondition(codeSection, variables, condition, newElseLabel, -1)) return;
                continue;
            }
            var mElse = Regex.Match(trimmed, @"^else:$");
            if (mElse.Success)
            {
                if (endIfStack.Count == 0) { Console.WriteLine($"Error: 'else' without 'if'."); return; }
                string endIfLabel = endIfStack.Peek();
                codeSection.Add($"jmp {endIfLabel}");

                // Define the label that the previous block was jumping to.
                codeSection.Add($"{elseStack.Pop()}:");

                // No more conditions can follow. Push a marker so we don't try to define another label.
                elseStack.Push("NO_ELSE");
                continue;
            }

            // === REPEAT LOOP START ===
            var mRepeat = Regex.Match(trimmed, @"^repeat (\d+) times$");
            if (mRepeat.Success)
            {
                int count = int.Parse(mRepeat.Groups[1].Value);
                string loopLabel = $"loop_{uniqueId++}";
                string endLoopLabel = $"end_loop_{uniqueId++}";

                // set counter
                codeSection.Add($"mov ebx, {count}");
                // start label
                codeSection.Add($"{loopLabel}:");

                // remember labels for closing bracket
                blockEndStack.Push(endLoopLabel);
                loopLabelStack.Push(loopLabel);
                i++;
                continue;
            }

            // === BLOCK END (for loops and such) ===
            if (trimmed == "]")
            {
                // handle end of repeat loop
                if (loopLabelStack.Count > 0 && blockEndStack.Count > 0)
                {
                    string loopLabel = loopLabelStack.Pop();
                    string endLoopLabel = blockEndStack.Pop();

                    codeSection.Add("dec ebx");
                    codeSection.Add($"jnz {loopLabel}");
                    codeSection.Add($"{endLoopLabel}:");
                    continue;
                }

                continue;
            }


            var mInput = Regex.Match(trimmed, @"^store console input in (\w+)\.$");
            if (mInput.Success)
            {
                string varName = mInput.Groups[1].Value;
                if (!variables.ContainsKey(varName))
                {
                    Console.WriteLine($"Error: variable '{varName}' not defined.");
                    return;
                }

                if (variableTypes.ContainsKey(varName) && variableTypes[varName] == "string")
                {
                    // --- FINAL FIX: Use Windows API ReadConsoleA ---
                    string charsReadLabel = $"chars_read_{uniqueId++}";
                    bssSection.Add($"{charsReadLabel} resd 1"); // Reserve space to store number of chars read

                    // ReadConsoleA(hConsoleInput, buffer, size, &charsRead, NULL)
                    codeSection.Add("push 0");                         // Reserved parameter, must be NULL
                    codeSection.Add($"push {charsReadLabel}");         // Pointer to store number of chars read
                    codeSection.Add("push 255");                       // Max chars to read (leave room for null)
                    codeSection.Add($"push {variables[varName]}");      // Buffer to read into
                    codeSection.Add("push dword [hConsoleInput]");     // Console input handle
                    codeSection.Add("call ReadConsoleA");
                    codeSection.Add("add esp, 20");                    // Clean up 5 arguments (20 bytes)

                    // --- Post-processing: Null-terminate the string ---
                    // The number of chars read is in our variable. We use it as an index.
                    codeSection.Add($"mov ecx, [{charsReadLabel}]"); // Get number of chars read
                    codeSection.Add($"mov esi, {variables[varName]}"); // Get pointer to our string buffer
                    // ReadConsoleA includes the carriage return (\r), so we subtract 2 to get to the last real character
                    codeSection.Add("sub ecx, 2");
                    codeSection.Add("mov byte [esi + ecx], 0"); // Place a null terminator at the end
                }
                else
                {
                    codeSection.Add($"push {variables[varName]}");
                    codeSection.Add("push format_number_input");
                    codeSection.Add("call scanf");
                    codeSection.Add("add esp, 8");
                }
                continue;
            }

            var mRunProcess = Regex.Match(trimmed, @"^run process ""(.*)""\.$");
            if (mRunProcess.Success)
            {
                string processPath = mRunProcess.Groups[1].Value;
                string cmdLabel = $"proc_cmd_{strCount++}";

                // --- FIX: Escape backslashes for the assembler ---
                string escapedPath = processPath.Replace("\\", "\\\\");

                // Add the escaped process path string to the data section
                dataSection.Add($"{cmdLabel} db \"{escapedPath}\",0");

                // --- Generate assembly code to call CreateProcessA ---
                // ...
                codeSection.Add("push process_info");        // lpProcessInformation
                codeSection.Add("push startup_info");        // lpStartupInfo
                codeSection.Add("push 0");                   // lpCurrentDirectory
                codeSection.Add("push 0");                   // lpEnvironment
                codeSection.Add("push 0");                   // dwCreationFlags
                codeSection.Add("push 0");                   // bInheritHandles
                codeSection.Add("push 0");                   // lpThreadAttributes
                codeSection.Add("push 0");                   // lpProcessAttributes
                codeSection.Add("push 0");                   // lpCommandLine
                codeSection.Add($"push {cmdLabel}");         // lpApplicationName
                codeSection.Add("call CreateProcessA");
                codeSection.Add("add esp, 40");              // Clean up 10 arguments (10 * 4 bytes)
                continue;
            }

            // Modified regex to accept both variables and quoted text
            var mWriteToFile = Regex.Match(trimmed, @"^write (""(.+)""|(\w+)) to ""(.+)""\.$");
            if (mWriteToFile.Success)
            {
                string quotedText = mWriteToFile.Groups[2].Value;  // Text in quotes
                string varName = mWriteToFile.Groups[3].Value;     // Variable name
                string filePath = mWriteToFile.Groups[4].Value;    // File path

                bool isQuotedText = !string.IsNullOrEmpty(quotedText);

                // If it's a variable, check if it exists
                if (!isQuotedText && !variables.ContainsKey(varName))
                {
                    Console.WriteLine($"Error: variable '{varName}' not defined.");
                    return;
                }

                string pathLabel = $"file_path_{strCount++}";
                string dirLabel = $"dir_path_{strCount++}";
                string bytesWrittenLabel = $"bytes_written_{uniqueId++}";

                // Escape backslashes for the assembler
                string escapedPath = filePath.Replace("\\", "\\\\");
                string escapedDir = System.IO.Path.GetDirectoryName(filePath)?.Replace("\\", "\\\\") ?? "";

                // Add labels to data section
                dataSection.Add($"{pathLabel} db \"{escapedPath}\",0");
                if (!string.IsNullOrEmpty(escapedDir))
                {
                    dataSection.Add($"{dirLabel} db \"{escapedDir}\",0");
                }
                bssSection.Add($"{bytesWrittenLabel} resd 1");

                // Create directory if it doesn't exist (only if there's a directory part)
                if (!string.IsNullOrEmpty(escapedDir))
                {
                    codeSection.Add("push 0");                    // lpSecurityAttributes
                    codeSection.Add($"push {dirLabel}");          // lpPathName
                    codeSection.Add("call CreateDirectoryA");
                    codeSection.Add("add esp, 8");                // Clean up arguments
                }

                // Create/open file for writing
                codeSection.Add("push 0");                        // hTemplateFile
                codeSection.Add("push 0x80");                     // FILE_ATTRIBUTE_NORMAL
                codeSection.Add("push 2");                        // CREATE_ALWAYS (overwrite if exists)
                codeSection.Add("push 0");                        // lpSecurityAttributes
                codeSection.Add("push 0");                        // FILE_SHARE_READ
                codeSection.Add("push 0x40000000");               // GENERIC_WRITE
                codeSection.Add($"push {pathLabel}");             // lpFileName
                codeSection.Add("call CreateFileA");
                codeSection.Add("add esp, 28");                   // Clean up 7 arguments
                codeSection.Add("mov edi, eax");                  // Store file handle in edi

                // Check if file creation was successful (handle != INVALID_HANDLE_VALUE)
                codeSection.Add("cmp eax, -1");
                string skipWriteLabel = $"skip_write_{uniqueId++}";
                codeSection.Add($"je {skipWriteLabel}");          // Jump if file creation failed

                if (isQuotedText)
                {
                    // Handle quoted text
                    string textLabel = $"text_content_{strCount++}";
                    string escapedText = quotedText.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    dataSection.Add($"{textLabel} db \"{escapedText}\",0");

                    // Calculate text length
                    string lenLabel = $"text_len_{uniqueId++}";
                    bssSection.Add($"{lenLabel} resd 1");

                    codeSection.Add($"mov esi, {textLabel}");        // Source string
                    codeSection.Add("xor ecx, ecx");                 // Counter = 0
                    string lenLoopLabel = $"len_loop_{uniqueId++}";
                    codeSection.Add($"{lenLoopLabel}:");
                    codeSection.Add("cmp byte [esi + ecx], 0");     // Check for null terminator
                    string lenDoneLabel = $"len_done_{uniqueId++}";
                    codeSection.Add($"je {lenDoneLabel}");
                    codeSection.Add("inc ecx");
                    codeSection.Add($"jmp {lenLoopLabel}");
                    codeSection.Add($"{lenDoneLabel}:");
                    codeSection.Add($"mov [{lenLabel}], ecx");      // Store length

                    // Write text to file
                    codeSection.Add("push 0");                      // lpOverlapped
                    codeSection.Add($"push {bytesWrittenLabel}");   // lpNumberOfBytesWritten
                    codeSection.Add($"push dword [{lenLabel}]");    // nNumberOfBytesToWrite
                    codeSection.Add($"push {textLabel}");           // lpBuffer
                    codeSection.Add("push edi");                    // hFile
                    codeSection.Add("call WriteFile");
                    codeSection.Add("add esp, 20");                 // Clean up 5 arguments
                }
                else
                {
                    // Handle variable
                    if (variableTypes.ContainsKey(varName) && variableTypes[varName] == "string")
                    {
                        // For string variables, we need to calculate the length first
                        string lenLabel = $"str_len_{uniqueId++}";
                        bssSection.Add($"{lenLabel} resd 1");

                        // Calculate string length
                        codeSection.Add($"mov esi, {variables[varName]}");  // Source string
                        codeSection.Add("xor ecx, ecx");                     // Counter = 0
                        string lenLoopLabel = $"len_loop_{uniqueId++}";
                        codeSection.Add($"{lenLoopLabel}:");
                        codeSection.Add("cmp byte [esi + ecx], 0");         // Check for null terminator
                        string lenDoneLabel = $"len_done_{uniqueId++}";
                        codeSection.Add($"je {lenDoneLabel}");
                        codeSection.Add("inc ecx");
                        codeSection.Add($"jmp {lenLoopLabel}");
                        codeSection.Add($"{lenDoneLabel}:");
                        codeSection.Add($"mov [{lenLabel}], ecx");          // Store length

                        // Write string to file
                        codeSection.Add("push 0");                          // lpOverlapped
                        codeSection.Add($"push {bytesWrittenLabel}");       // lpNumberOfBytesWritten
                        codeSection.Add($"push dword [{lenLabel}]");        // nNumberOfBytesToWrite
                        codeSection.Add($"push {variables[varName]}");      // lpBuffer
                        codeSection.Add("push edi");                        // hFile
                        codeSection.Add("call WriteFile");
                        codeSection.Add("add esp, 20");                     // Clean up 5 arguments
                    }
                    else
                    {
                        // For numeric variables, convert to string first
                        string numStrLabel = $"num_str_{uniqueId++}";
                        bssSection.Add($"{numStrLabel} resb 12");           // Buffer for number string

                        string formatLabel = $"num_format_{strCount++}";
                        dataSection.Add($"{formatLabel} db \"%d\",0");

                        // Convert number to string using sprintf
                        codeSection.Add($"push dword [{variables[varName]}]"); // Number value
                        codeSection.Add($"push {formatLabel}");                // Format string
                        codeSection.Add($"push {numStrLabel}");                // Destination buffer
                        codeSection.Add("call sprintf");
                        codeSection.Add("add esp, 12");                        // Clean up arguments

                        // Calculate length of number string
                        codeSection.Add("mov ecx, eax");                       // sprintf returns length

                        // Write number string to file
                        codeSection.Add("push 0");                             // lpOverlapped
                        codeSection.Add($"push {bytesWrittenLabel}");          // lpNumberOfBytesWritten
                        codeSection.Add("push ecx");                           // nNumberOfBytesToWrite
                        codeSection.Add($"push {numStrLabel}");                // lpBuffer
                        codeSection.Add("push edi");                           // hFile
                        codeSection.Add("call WriteFile");
                        codeSection.Add("add esp, 20");                        // Clean up 5 arguments
                    }
                }

                // Close file handle
                codeSection.Add("push edi");                        // hObject (file handle)
                codeSection.Add("call CloseHandle");
                codeSection.Add("add esp, 4");                      // Clean up argument

                codeSection.Add($"{skipWriteLabel}:");              // Label for skipping write on error
                continue;
            }

            // Add this pattern matching in CompileCodeBlock method
            var mTurnToText = Regex.Match(trimmed, @"^turn (\w+) to text as (\w+)\.$");
            if (mTurnToText.Success)
            {
                string sourceVar = mTurnToText.Groups[1].Value;
                string targetVar = mTurnToText.Groups[2].Value;

                if (!variables.ContainsKey(sourceVar))
                {
                    Console.WriteLine($"Error: variable '{sourceVar}' not defined.");
                    return;
                }

                // Check if target variable already exists
                if (variables.ContainsKey(targetVar))
                {
                    Console.WriteLine($"Error: variable '{targetVar}' already exists.");
                    return;
                }

                // Check if source is already a string
                if (variableTypes.ContainsKey(sourceVar) && variableTypes[sourceVar] == "string")
                {
                    Console.WriteLine($"Error: variable '{sourceVar}' is already a string. Cannot convert string to text.");
                    return;
                }

                // Generate unique labels
                int cmdId = uniqueId++;
                string formatLabel = $"format_{cmdId}";
                string newStringLabel = $"str_{targetVar}_{cmdId}";

                // Add format string to data section
                dataSection.Add($"{formatLabel} db \"%d\",0");

                // Reserve space for the new string (12 bytes for any 32-bit integer + null terminator)
                bssSection.Add($"{newStringLabel} resb 12");

                // Convert number to string using sprintf
                codeSection.Add($"push dword [{variables[sourceVar]}]"); // Source number value
                codeSection.Add($"push {formatLabel}");                  // Format string "%d"
                codeSection.Add($"push {newStringLabel}");               // Destination buffer
                codeSection.Add("call sprintf");
                codeSection.Add("add esp, 12");                          // Clean up arguments

                // Register the new string variable
                variables[targetVar] = newStringLabel;
                variableTypes[targetVar] = "string";

                continue;
            }




            Console.WriteLine($"Syntax error: unknown command '{trimmed}'");
            return;
        }

        // Close any remaining open blocks from this code block
        while (elseStack.Count > 0)
        {
            if (elseStack.Peek() != "NO_ELSE")
            {
                codeSection.Add($"{elseStack.Pop()}:");
            }
            else
            {
                elseStack.Pop();
            }
        }
        while (endIfStack.Count > 0) { codeSection.Add($"{endIfStack.Pop()}:"); }
        while (loopLabelStack.Count > 0)
        {
            string label = loopLabelStack.Pop();
            codeSection.Add("dec ebx");
            codeSection.Add($"jnz {label}");
        }
    }

    // --- Parses the file to find all functions and global variables ---
    static void ParseFunctionsAndGlobals(string[] lines)
    {
        string currentFunctionName = null;
        List<string> currentFunctionBody = null;
        bool inFunction = false;

        // --- FIX: Move input_buffer to the .bss section ---
        // It's more efficient to allocate it at runtime as uninitialized space.
        bssSection.Add("input_buffer resb 256");
        bssSection.Add("temp_char resb 1");

        // --- CHANGE: Add a variable for the console handle ---
        bssSection.Add("hConsoleInput resd 1");

        // Structures for the CreateProcessA function
        bssSection.Add("startup_info resb 68"); // STARTUPINFO structure (68 bytes)
        bssSection.Add("process_info resd 16"); // PROCESS_INFORMATION structure (4 dwords = 16 bytes)

        // dataSection.Add("format_string_input db \" %255[^\\n]\",0");
        dataSection.Add("format_number_input db \"%d\",0");
        dataSection.Add("format_char db \"%c\",0");



        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

            if (trimmed.StartsWith("define function named"))
            {
                currentFunctionName = Regex.Match(trimmed, @"^define function named (\w+)$").Groups[1].Value;
                currentFunctionBody = new List<string>();
                inFunction = true;
                continue;
            }

            if (inFunction)
            {
                currentFunctionBody.Add(line);
                if (trimmed == "]")
                {
                    functionBodies[currentFunctionName] = currentFunctionBody;
                    inFunction = false;
                    currentFunctionName = null;
                    currentFunctionBody = null;
                }
                continue;
            }

            var mSet = Regex.Match(trimmed, @"^define (\w+) as (.+)\.$");
            if (mSet.Success)
            {
                string varName = mSet.Groups[1].Value;
                string valueStr = mSet.Groups[2].Value;
                string label = $"var_{varName}";
                variables[varName] = label;

                if (valueStr.StartsWith("\"") && valueStr.EndsWith("\""))
                {
                    variableTypes[varName] = "string";
                    string text = valueStr.Trim('"');

                    // --- FIX: Move the string variable buffer to the .bss section ---
                    bssSection.Add($"{label} resb 256"); // Reserve 256 writable bytes in BSS

                    // The initial value remains in the read-only .data section
                    string initLabel = $"init_{label}";
                    dataSection.Add($"{initLabel} db \"{text}\",0");

                    stringInits.Add((label, initLabel));
                }
                else if (valueStr == "true") { variableTypes[varName] = "int"; dataSection.Add($"{label} dd 1"); }
                else if (valueStr == "false") { variableTypes[varName] = "int"; dataSection.Add($"{label} dd 0"); }
                else { variableTypes[varName] = "int"; dataSection.Add($"{label} dd {valueStr}"); }
            }
        }
    }

    // --- NEW: Helper method to write strings byte-by-byte to avoid all C# string issues ---
    static void WriteStringToDataSection(string label, string text)
    {
        dataSection.Add($"{label} db ");
        foreach (char c in text)
        {
            if (c == '"')
            {
                dataSection.Add("'\"'"); // Write escaped quote
            }
            else if (c == '\\')
            {
                dataSection.Add("'\\\\'"); // Write escaped backslash
            }
            else
            {
                dataSection.Add($"'{c}'"); // Write the character
            }
        }
        dataSection.Add(",0"); // Write the null terminator
    }

    static bool CompileCondition(List<string> codeSection, Dictionary<string, string> variables, string condition, string falseLabel, int lineNumber)
    {
        var mBool = Regex.Match(condition, @"(\w+) is (true|false)$");
        if (mBool.Success)
        {
            string varName = mBool.Groups[1].Value;
            string boolValue = mBool.Groups[2].Value;
            if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return false; }
            string varLabel = variables[varName];
            codeSection.Add($"mov eax, [{varLabel}]");
            if (boolValue == "true") { codeSection.Add($"cmp eax, 0"); codeSection.Add($"je {falseLabel}"); }
            else { codeSection.Add($"cmp eax, 0"); codeSection.Add($"jne {falseLabel}"); }
            return true;
        }
        var mCompare = Regex.Match(condition, @"(\w+) is (equal to|not equal to|greater than|less than) (\w+)");
        if (mCompare.Success)
        {
            string var1Name = mCompare.Groups[1].Value;
            string op = mCompare.Groups[2].Value;
            string operand2 = mCompare.Groups[3].Value;
            if (!variables.ContainsKey(var1Name)) { Console.WriteLine($"Error: variable '{var1Name}' not defined."); return false; }
            string var1Label = variables[var1Name];
            codeSection.Add($"mov eax, [{var1Label}]");
            if (int.TryParse(operand2, out int literalValue)) { codeSection.Add($"cmp eax, {literalValue}"); }
            else if (operand2 == "true" || operand2 == "false") { int boolValue = (operand2 == "true") ? 1 : 0; codeSection.Add($"cmp eax, {boolValue}"); }
            else { if (!variables.ContainsKey(operand2)) { Console.WriteLine($"Error: variable '{operand2}' not defined."); return false; } string var2Label = variables[operand2]; codeSection.Add($"cmp eax, [{var2Label}]"); }
            switch (op) { case "equal to": codeSection.Add($"jne {falseLabel}"); break; case "not equal to": codeSection.Add($"je {falseLabel}"); break; case "greater than": codeSection.Add($"jle {falseLabel}"); break; case "less than": codeSection.Add($"jge {falseLabel}"); break; }
            return true;
        }
        Console.WriteLine($"Syntax error in condition: '{condition}'");
        return false;
    }
}