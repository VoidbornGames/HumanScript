<div class="container">
<h1 id="humanscript">HumanScript</h1>
<p>An English-like programming language that compiles to a Windows executable. HumanScript is designed for simplicity and readability, abstracting away complex syntax while providing core programming functionalities like variables, control flow, file operations, and system interaction.</p>

<h2 id="supported-os">Supported OS's</h2>
<ul>
<li>Windows 10</li>
<li>Windows 11</li>
<li>Windows Server (2016+)</li>
</ul>

<h2 id="table-of-contents">Table of Contents</h2>
<ul>
<li><a href="#features">Features</a></li>
<li><a href="#requirements">Requirements</a></li>
<li><a href="#installation">Installation</a></li>
<li><a href="#getting-started">Getting Started</a></li>
<li><a href="#language-syntax">Language Syntax</a></li>
<li><a href="#how-it-works">How It Works</a></li>
<li><a href="#contributing">Contributing</a></li>
<li><a href="#license">License</a></li>
</ul>

<h2 id="features">Features</h2>
<div class="feature-list">
<div class="feature-card">
<h3 id="-english-like-syntax">🌍 English-like Syntax</h3>
<p>Write code that reads like plain English, making it accessible to beginners and non-programmers.</p>
<ul>
<li><strong>Variables</strong>: <code>define name as "John".</code></li>
<li><strong>Printing</strong>: <code>print "Hello, World!".</code></li>
<li><strong>Comments</strong>: <code># This is a comment.</code></li>
</ul>
</div>

<div class="feature-card">
<h3 id="-variables--operations">🧮 Variables & Operations</h3>
<p>Work with numbers and strings using intuitive commands.</p>
<ul>
<li><strong>Arithmetic</strong>: <code>add 5 to score.</code> or <code>set total to price plus tax.</code></li>
<li><strong>String Concatenation</strong>: <code>set full_name to first_name combined with last_name.</code></li>
<li><strong>Type Conversion</strong>: <code>turn age to text as age_text.</code></li>
</ul>
</div>

<div class="feature-card">
<h3 id="-control-flow">🔄 Control Flow</h3>
<p>Implement logic with natural language conditions and loops.</p>
<ul>
<li><strong>Conditionals</strong>: <code>if age is greater than 18:</code>, <code>else if:</code>, <code>else:</code></li>
<li><strong>Loops</strong>: <code>repeat 5 times</code> ... <code>]</code></li>
</ul>
</div>

<div class="feature-card">
<h3 id="-system-interaction">🖥️ System Interaction</h3>
<p>Interact with the underlying operating system and other processes.</p>
<ul>
<li><strong>Run Processes</strong>: <code>run process "notepad.exe".</code></li>
<li><strong>File I/O</strong>: <code>write "Log entry" to "C:\logs\app.log".</code></li>
<li><strong>Pause Execution</strong>: <code>wait for 3 seconds.</code></li>
</ul>
</div>

<div class="feature-card">
<h3 id="-functions">🔧 Functions</h3>
<p>Organize your code into reusable blocks.</p>
<ul>
<li><strong>Define Function</strong>: <code>define function named say_hello</code> ... <code>]</code></li>
<li><strong>Call Function</strong>: <code>run function say_hello.</code></li>
</ul>
</div>

<div class="feature-card">
<h3 id="-inputoutput">📝 Input/Output</h3>
<p>Communicate with the user via the console.</p>
<ul>
<li><strong>Get Input</strong>: <code>store console input in user_name.</code></li>
<li><strong>Print Output</strong>: <code>print "Welcome, ".</code> <code>print user_name.</code></li>
</ul>
</div>
</div>

<h2 id="requirements">Requirements</h2>
<p>To compile HumanScript code, you need the following tools:</p>
<ul>
<li><strong>Windows OS</strong> (to run the compiler and the output)</li>
<li><strong>.NET 8 SDK</strong> (to build the HumanScript compiler from source)</li>
<li><strong><a href="https://www.nasm.us/">NASM (The Netwide Assembler)</a></strong>: For assembling the generated assembly code.</li>
<li><strong><a href="http://www.godevtool.com/">GoLink</a></strong>: For linking the object file into a final executable.</li>
</ul>
<p><em>Note: The compiler looks for <code>nasm.exe</code> in a <code>NASM\</code> folder and <code>GoLink.exe</code> in a <code>Golink\</code> folder relative to its location. Alternatively, you can place them in your system's PATH.</em></p>

<h2 id="installation">Installation</h2>
<ol>
<li><strong>Clone the repository</strong>:
<pre><code class="language-bash">git clone https://github.com/your-username/HumanScript.git
cd HumanScript
</code></pre>
</li>
<li><strong>Download Dependencies</strong>:
<ul>
<li>Download NASM and extract <code>nasm.exe</code> to a folder named <code>NASM</code> in the project root.</li>
<li>Download GoLink and extract <code>GoLink.exe</code> to a folder named <code>Golink</code> in the project root.</li>
</ul>
</li>
<li><strong>Compile the Compiler</strong>:
<pre><code class="language-bash">dotnet build
</code></pre>
<p>The compiled <code>HumanScript.exe</code> will be in the <code>bin\Debug\net8.0</code> directory. You can copy it to the project root for convenience.</p>
</li>
</ol>

<h2 id="getting-started">Getting Started</h2>
<ol>
<li><strong>Create a HumanScript file</strong> (e.g., <code>hello.eng</code>):
<pre><code class="language-human"># hello.eng
define message as "Hello from HumanScript!".
print message.
</code></pre>
</li>
<li><strong>Compile your script</strong>:
<pre><code class="language-bash">HumanScript.exe hello.eng
</code></pre>
</li>
<li><strong>Run the resulting executable</strong>:
<pre><code class="language-bash">hello.exe
</code></pre>
<p><strong>Output:</strong></p>
<pre><code>Hello from HumanScript!
</code></pre>
</li>
</ol>

<h2 id="language-syntax">Language Syntax</h2>
<p>Here is a quick reference for the available commands in HumanScript.</p>

<h4>Variables</h4>
<pre><code class="language-human">define my_number as 42.
define my_string as "This is a string".
define is_ready as true.
</code></pre>

<h4>Arithmetic</h4>
<pre><code class="language-human">define counter as 0.
add 10 to counter.
subtract 5 from counter.
multiply counter by 2.
divide counter by 3.

# Set variable to a result
set total to price plus tax.
set result to base_value times multiplier.
</code></pre>

<h4>Strings</h4>
<pre><code class="language-human">define first_name as "John".
define last_name as "Doe".
define full_name as "".

# Concatenate strings
set full_name to first_name combined with last_name.
print full_name. # Outputs: JohnDoe

# Convert number to string
define age as 30.
define age_text as "".
turn age to text as age_text.
</code></pre>

<h4>Control Flow</h4>
<pre><code class="language-human">define score as 85.

if score is greater than 90:
    print "Excellent!".
else if score is greater than 70:
    print "Good job!".
else:
    print "Keep trying".

repeat 3 times
[
    print "This will print 3 times".
]
</code></pre>

<h4>Functions</h4>
<pre><code class="language-human">define function named greet
[
    print "Hello there!".
]

# Main program
print "Calling a function...".
run function greet.
print "Function finished.".
</code></pre>

<h4>System & File Operations</h4>
<pre><code class="language-human"># Run an external program
run process "calc.exe".

# Wait for 2 seconds
wait for 2 seconds.

# Write a literal string to a file
write "Log entry at 10:00 AM" to "C:\temp\log.txt".

# Write a variable to a file
define user_data as "User: Alice".
write user_data to "C:\temp\users.txt".
</code></pre>

<h2 id="how-it-works">How It Works</h2>
<p>The HumanScript compiler translates your readable <code>.eng</code> file into a native Windows executable through a multi-step process:</p>
<ol>
<li><strong>Parsing</strong>: <code>HumanScript.exe</code> reads your <code>.eng</code> source file and validates the syntax.</li>
<li><strong>Code Generation</strong>: It generates 32-bit x86 assembly code, saving it as <code>temp.asm</code>.</li>
<li><strong>Assembly</strong>: It calls <strong>NASM</strong> to assemble <code>temp.asm</code> into an object file (<code>temp.obj</code>).</li>
<li><strong>Linking</strong>: It calls <strong>GoLink</strong> to link the object file with necessary system libraries (<code>msvcrt.dll</code>, <code>kernel32.dll</code>) to produce the final <code>.exe</code>.</li>
</ol>
<pre><code>Your Code (.eng) -> HumanScript Compiler -> Assembly (.asm) -> NASM -> Object File (.obj) -> GoLink -> Executable (.exe)
</code></pre>

<h2 id="contributing">Contributing</h2>
<p>Contributions are welcome! If you have a feature request, bug report, or a pull request, please open an issue or submit a pull request.</p>
<ol>
<li>Fork the repository.</li>
<li>Create a new branch (<code>git checkout -b feature/amazing-feature</code>).</li>
<li>Commit your changes (<code>git commit -m 'Add some amazing feature'</code>).</li>
<li>Push to the branch (<code>git push origin feature/amazing-feature</code>).</li>
<li>Open a Pull Request.</li>
</ol>

<h2 id="license">License</h2>
<p>This project is licensed under the MIT License - see the <a href="LICENSE">LICENSE</a> file for details.</p>
</div>
