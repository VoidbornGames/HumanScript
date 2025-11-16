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
    static List<string> nimCode = new List<string>();
    static List<string> functionCode = new List<string>();
    static Stack<int> ifCountStack = new Stack<int>();
    static Stack<int> loopCountStack = new Stack<int>();
    static int strCount = 0;
    static int ifCount = 0;
    static int loopCount = 0;
    static int uniqueId = 0;
    static bool inMainCode = true;

    static void Main(string[] mainArgs)
    {
        string inputFile = "script.eng";
        string nimFile = "temp.nim";
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

        // --- Parse all main variables ---


        // --- Initialize Nim code with imports and declarations ---
        InitializeNimCode();

        // --- PASS 2: Separate main code from functions ---
        var mainProgramLines = new List<string>();
        inMainCode = true;
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

        // --- Compile the main program ---
        nimCode.Add("proc main() =");
        CompileCodeBlock(mainProgramLines, 2);
        nimCode.Add("  discard");
        nimCode.Add("");

        // --- Add function definitions ---
        nimCode.AddRange(functionCode);

        // --- Add main procedure call ---
        nimCode.Add("main()");

        // --- Write Nim file ---
        using (var sw = new StreamWriter(nimFile))
        {
            foreach (var line in nimCode)
            {
                sw.WriteLine(line);
            }
        }

        // --- Compile Nim code ---
        Console.WriteLine("Compiling with Nim...");

        var psi = new ProcessStartInfo();
        psi.FileName = @"C:\Users\Naboodi\source\repos\HumanScript\HumanScript\bin\Release\net8.0\nim-2.2.6\bin\nim.exe";

        psi.Arguments =
            @"c --gcc.exe=""C:\TDM-GCC-64\bin\gcc.exe"" " +
            @"--linker.exe=""C:\TDM-GCC-64\bin\ld.exe"" " +
            @"-d:release --hints:off --nimcache=.nimcache temp.nim";

        psi.EnvironmentVariables["PATH"] =
            @"C:\TDM-GCC-64\bin;" +
            @"C:\TDM-GCC-64\x86_64-w64-mingw32\bin;" +
            Environment.GetEnvironmentVariable("PATH");


        psi.WorkingDirectory = @"C:\Users\Naboodi\source\repos\HumanScript\HumanScript\bin\Release\net8.0";
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        var process = new Process();
        process.StartInfo = psi;

        process.Start();

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        Console.WriteLine(" ");
        if (!string.IsNullOrEmpty(output))
            Console.WriteLine(output);
        if (!string.IsNullOrEmpty(error))
            Console.WriteLine(error);

        if (File.Exists(exeFile))
        {
            Console.WriteLine($"Compilation finished: {exeFile}");
        }
        else if (error.Contains("ERROR"))
        {
            Console.WriteLine("Compilation failed.");
        }
    }

    static void InitializeNimCode()
    {
        nimCode.Add("import os");
        nimCode.Add("import strutils");
        nimCode.Add("import strformat");
        nimCode.Add("import times");
        nimCode.Add("import streams");
        nimCode.Add("");
    }

    // --- Core compilation logic for a block of code ---
    static void CompileCodeBlock(List<string> lines, int indentLevel)
    {
        string indent = new string(' ', indentLevel);
        var blockEndStack = new Stack<string>();
        var endIfStack = new Stack<string>();
        var elseStack = new Stack<string>();
        var loopLabelStack = new Stack<string>();
        int prevIndent = indentLevel;

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

            int indentSpaces = line.Length - line.TrimStart().Length;
            string trimmed = line.Trim();

            // Handle dedent (closing blocks)
            while (indentSpaces < prevIndent)
            {
                if (endIfStack.Count > 0)
                {
                    string endIfLabel = endIfStack.Pop();
                }
                prevIndent -= 4;
            }
            prevIndent = indentSpaces;

            // --- Variable assignment with number ---
            var mSetNum = Regex.Match(trimmed, @"^set (\w+) to (\d+)\.$");
            if (mSetNum.Success)
            {
                string varName = mSetNum.Groups[1].Value;
                string value = mSetNum.Groups[2].Value;
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }
                nimCode.Add($"{indent}{varName} = {value}");
                continue;
            }

            // --- Math operations: add/subtract/multiply/divide number to/from/by variable ---
            var mMathOp = Regex.Match(trimmed, @"^(add|subtract|multiply|divide)\s+(\d+|\w+)\s+(to|from|by)\s+(\w+)\.$");
            if (mMathOp.Success)
            {
                string op = mMathOp.Groups[1].Value;
                string value = mMathOp.Groups[2].Value;
                string preposition = mMathOp.Groups[3].Value;
                string varName = mMathOp.Groups[4].Value;

                if (!variables.ContainsKey(varName))
                {
                    Console.WriteLine($"Error: variable '{varName}' not defined.");
                    return;
                }

                // Check if value is a variable or literal
                bool valueIsVariable = variables.ContainsKey(value);

                switch (op)
                {
                    case "add":
                        if (valueIsVariable)
                            nimCode.Add($"{indent}{varName} += {value}");
                        else
                            nimCode.Add($"{indent}{varName} += {value}");
                        break;
                    case "subtract":
                        if (valueIsVariable)
                            nimCode.Add($"{indent}{varName} -= {value}");
                        else
                            nimCode.Add($"{indent}{varName} -= {value}");
                        break;
                    case "multiply":
                        if (valueIsVariable)
                            nimCode.Add($"{indent}{varName} *= {value}");
                        else
                            nimCode.Add($"{indent}{varName} *= {value}");
                        break;
                    case "divide":
                        if (valueIsVariable)
                            nimCode.Add($"{indent}{varName} = {varName} div {value}");
                        else
                            nimCode.Add($"{indent}{varName} = {varName} div {value}");
                        break;
                }
                continue;
            }

            // --- String concatenation ---
            var mStringConcat = Regex.Match(trimmed, @"^set (\w+) to (\w+) combined with (\w+)\.$");
            if (mStringConcat.Success)
            {
                string destVar = mStringConcat.Groups[1].Value;
                string srcVar1 = mStringConcat.Groups[2].Value;
                string srcVar2 = mStringConcat.Groups[3].Value;

                if (!variables.ContainsKey(destVar) || !variables.ContainsKey(srcVar1) || !variables.ContainsKey(srcVar2))
                {
                    Console.WriteLine($"Error in string concatenation: one or more variables not defined.");
                    return;
                }

                nimCode.Add($"{indent}{destVar} = {srcVar1} & {srcVar2}");
                continue;
            }

            // --- Print variable ---
            var mPrint = Regex.Match(trimmed, @"^print (\w+)\.$");
            if (mPrint.Success)
            {
                string varName = mPrint.Groups[1].Value;
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }
                nimCode.Add($"{indent}echo {varName}");
                continue;
            }

            // --- Print string literal ---
            var mPrintStr = Regex.Match(trimmed, @"^print ""(.*)""\.$");
            if (mPrintStr.Success)
            {
                string text = mPrintStr.Groups[1].Value;
                nimCode.Add($"{indent}echo \"{text}\"");
                continue;
            }

            // --- Function call ---
            var mRunFunc = Regex.Match(trimmed, @"^run function (\w+)\.$");
            if (mRunFunc.Success)
            {
                string funcName = mRunFunc.Groups[1].Value;
                if (!functionBodies.ContainsKey(funcName))
                {
                    Console.WriteLine($"Error: function '{funcName}' is not defined.");
                    return;
                }
                nimCode.Add($"{indent}{funcName}()");
                continue;
            }

            // --- Variable definition (skip in compilation, already handled) ---
            var mSet = Regex.Match(trimmed, @"^define (\w+) as (.+)\.$");
            if (mSet.Success) continue;

            // --- Wait/sleep ---
            var mWait = Regex.Match(trimmed, @"^wait for (\d+) seconds\.$");
            if (mWait.Success)
            {
                int seconds = int.Parse(mWait.Groups[1].Value);
                nimCode.Add($"{indent}sleep({seconds * 1000})");
                continue;
            }

            // --- Math operations with numbers: set var to num op num ---
            var mMathWithNumbers = Regex.Match(trimmed, @"^set (\w+) to (\d+) (times|plus|minus|divided by) (\d+)\.$");
            if (mMathWithNumbers.Success)
            {
                string destVar = mMathWithNumbers.Groups[1].Value;
                string num1 = mMathWithNumbers.Groups[2].Value;
                string op = mMathWithNumbers.Groups[3].Value;
                string num2 = mMathWithNumbers.Groups[4].Value;

                if (!variables.ContainsKey(destVar))
                {
                    Console.WriteLine($"Error: destination variable '{destVar}' not defined.");
                    return;
                }

                string nimOp = ConvertOperator(op);
                nimCode.Add($"{indent}{destVar} = {num1} {nimOp} {num2}");
                continue;
            }

            // --- Math operations with variables: set var to var op var ---
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

                string nimOp = ConvertOperator(op);
                nimCode.Add($"{indent}{destVar} = {srcVar1} {nimOp} {srcVar2}");
                continue;
            }

            // --- Mixed math operations ---
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

                string nimOp = ConvertOperator(op);
                nimCode.Add($"{indent}{destVar} = {operand1} {nimOp} {operand2}");
                continue;
            }

            // --- Boolean assignment ---
            var mSetBool = Regex.Match(trimmed, @"^set (\w+) to (true|false)\.$");
            if (mSetBool.Success)
            {
                string varName = mSetBool.Groups[1].Value;
                string value = mSetBool.Groups[2].Value;
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }
                nimCode.Add($"{indent}{varName} = {value}");
                continue;
            }

            // --- Division operation ---
            var mDivVarByNum = Regex.Match(trimmed, @"^divide (\w+) by (\d+)\.$");
            if (mDivVarByNum.Success)
            {
                string varName = mDivVarByNum.Groups[1].Value;
                string value = mDivVarByNum.Groups[2].Value;
                if (!variables.ContainsKey(varName)) { Console.WriteLine($"Error: variable '{varName}' not defined."); return; }
                nimCode.Add($"{indent}{varName} = {varName} div {value}");
                continue;
            }

            // --- If statement ---
            var mIf = Regex.Match(trimmed, @"^if (.+):$");
            if (mIf.Success)
            {
                string condition = mIf.Groups[1].Value;
                string nimCondition = ConvertCondition(condition);
                nimCode.Add($"{indent}if {nimCondition}:");
                endIfStack.Push("endif");
                continue;
            }

            // --- Else if statement ---
            var mElseIf = Regex.Match(trimmed, @"^else if (.+):$");
            if (mElseIf.Success)
            {
                if (endIfStack.Count == 0) { Console.WriteLine($"Error: 'else if' without 'if'."); return; }
                string condition = mElseIf.Groups[1].Value;
                string nimCondition = ConvertCondition(condition);
                nimCode.Add($"{indent}elif {nimCondition}:");
                continue;
            }

            // --- Else statement ---
            var mElse = Regex.Match(trimmed, @"^else:$");
            if (mElse.Success)
            {
                if (endIfStack.Count == 0) { Console.WriteLine($"Error: 'else' without 'if'."); return; }
                nimCode.Add($"{indent}else:");
                continue;
            }

            // --- Repeat loop ---
            var mRepeat = Regex.Match(trimmed, @"^repeat (\d+) times$");
            if (mRepeat.Success)
            {
                int count = int.Parse(mRepeat.Groups[1].Value);
                string loopVar = $"i{uniqueId++}";
                nimCode.Add($"{indent}for {loopVar} in 1..{count}:");
                blockEndStack.Push("endfor");
                i++; // Skip the opening bracket
                continue;
            }

            // --- Block end ---
            if (trimmed == "]")
            {
                if (blockEndStack.Count > 0)
                {
                    blockEndStack.Pop();
                }
                continue;
            }

            // --- Console input ---
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
                    nimCode.Add($"{indent}{varName} = readLine(stdin)");
                }
                else
                {
                    nimCode.Add($"{indent}{varName} = parseInt(readLine(stdin))");
                }
                continue;
            }

            // --- Run process ---
            var mRunProcess = Regex.Match(trimmed, @"^run process ""(.*)""\.$");
            if (mRunProcess.Success)
            {
                string processPath = mRunProcess.Groups[1].Value;
                nimCode.Add($"{indent}discard execShellCmd(\"{processPath}\")");
                continue;
            }

            // --- Write to file ---
            var mWriteToFile = Regex.Match(trimmed, @"^write (""(.+)""|(\w+)) to ""(.+)""\.$");
            if (mWriteToFile.Success)
            {
                string quotedText = mWriteToFile.Groups[2].Value;
                string varName = mWriteToFile.Groups[3].Value;
                string filePath = mWriteToFile.Groups[4].Value;

                bool isQuotedText = !string.IsNullOrEmpty(quotedText);
                string content = isQuotedText ? $"\"{quotedText}\"" : varName;

                // Create directory if it doesn't exist
                string dirPath = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dirPath))
                {
                    nimCode.Add($"{indent}createDir(\"{dirPath.Replace("\\", "\\\\")}\")");
                }

                nimCode.Add($"{indent}writeFile(\"{filePath.Replace("\\", "\\\\")}\", {content})");
                continue;
            }

            // --- Convert number to string ---
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

                if (!variables.ContainsKey(targetVar))
                {
                    Console.WriteLine($"Error: variable '{targetVar}' not defined.");
                    return;
                }

                nimCode.Add($"{indent}{targetVar} = $({sourceVar})");
                continue;
            }

            Console.WriteLine($"Syntax error: unknown command '{trimmed}'");
            return;
        }
    }

    // --- Helper method to convert operators to Nim syntax ---
    static string ConvertOperator(string op)
    {
        switch (op)
        {
            case "times": return "*";
            case "plus": return "+";
            case "minus": return "-";
            case "divided by": return "div";
            default: return op;
        }
    }

    // --- Helper method to convert conditions to Nim syntax ---
    static string ConvertCondition(string condition)
    {
        // Boolean condition: "x is true"
        var mBool = Regex.Match(condition, @"(\w+) is (true|false)$");
        if (mBool.Success)
        {
            string varName = mBool.Groups[1].Value;
            string boolValue = mBool.Groups[2].Value;
            if (boolValue == "true")
                return varName;
            else
                return $"not {varName}";
        }

        // Comparison condition: "x is equal to y"
        var mCompare = Regex.Match(condition, @"(\w+) is (equal to|not equal to|greater than|less than) (\w+|\d+)");
        if (mCompare.Success)
        {
            string var1 = mCompare.Groups[1].Value;
            string op = mCompare.Groups[2].Value;
            string var2 = mCompare.Groups[3].Value;

            string nimOp = "";
            switch (op)
            {
                case "equal to": nimOp = "=="; break;
                case "not equal to": nimOp = "!="; break;
                case "greater than": nimOp = ">"; break;
                case "less than": nimOp = "<"; break;
            }

            return $"{var1} {nimOp} {var2}";
        }

        return condition; // Return as-is if no pattern matched
    }

    // --- Parses the file to find all functions and global variables ---
    static void ParseFunctionsAndGlobals(string[] lines)
    {
        string currentFunctionName = null;
        List<string> currentFunctionBody = null;
        bool inFunction = false;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

            if (trimmed.StartsWith("define function named"))
            {
                currentFunctionName = Regex.Match(trimmed, @"^define function named (\w+)$").Groups[1].Value;
                currentFunctionBody = new List<string>();
                inFunction = true;

                // Start function definition in Nim
                functionCode.Add($"proc {currentFunctionName}() =");
                continue;
            }

            if (inFunction)
            {
                if (trimmed == "]")
                {
                    functionBodies[currentFunctionName] = currentFunctionBody;

                    // Compile function body
                    CompileFunctionBody(currentFunctionBody, currentFunctionName);

                    inFunction = false;
                    currentFunctionName = null;
                    currentFunctionBody = null;
                    functionCode.Add("");
                }
                else
                {
                    currentFunctionBody.Add(line);
                }
                continue;
            }

            var mSet = Regex.Match(trimmed, @"^define (\w+) as (.+)\.$");
            if (mSet.Success)
            {
                string varName = mSet.Groups[1].Value;
                string valueStr = mSet.Groups[2].Value;
                variables[varName] = varName;

                if (valueStr.StartsWith("\"") && valueStr.EndsWith("\""))
                {
                    variableTypes[varName] = "string";
                    string text = valueStr.Trim('"');
                    nimCode.Add($"var {varName}: string = \"{text}\"");
                }
                else if (valueStr == "true")
                {
                    variableTypes[varName] = "bool";
                    nimCode.Add($"var {varName}: bool = true");
                }
                else if (valueStr == "false")
                {
                    variableTypes[varName] = "bool";
                    nimCode.Add($"var {varName}: bool = false");
                }
                else
                {
                    variableTypes[varName] = "int";
                    nimCode.Add($"var {varName}: int = {valueStr}");
                }
            }
        }

        nimCode.Add("");
    }

    // --- Compile function body separately ---
    static void CompileFunctionBody(List<string> functionBody, string functionName)
    {
        var bodyLines = functionBody.Where(l => l.Trim() != "[" && l.Trim() != "]").ToList();

        // Add function body with proper indentation
        foreach (string line in bodyLines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Skip function definition line
            if (trimmed.StartsWith("define function named")) continue;

            // Process the line similar to main code but with function context
            ProcessFunctionLine(trimmed, 2);
        }
    }

    static void ProcessFunctionLine(string line, int indentLevel)
    {
        string indent = new string(' ', indentLevel);

        // This is a simplified version - you would need to implement the same
        // pattern matching as in CompileCodeBlock but for function context
        // For now, just echo the basic structure
        functionCode.Add($"{indent}# {line}");
    }
}
