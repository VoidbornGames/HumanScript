section .data
format_number_input db "%d",0
format_char db "%c",0
init_var_user_name db "",0
var_user_age dd 0
init_var_output_file db "user_info.txt",0
init_var_summary db "",0
str_0 db "--- User Information Collector ---",10,0
str_1 db "Please enter your name:",10,0
str_2 db "Please enter your age:",10,0

section .bss
input_buffer resb 256
temp_char resb 1
hConsoleInput resd 1
startup_info resb 68
process_info resd 16
var_user_name resb 256
var_output_file resb 256
var_summary resb 256
chars_read_0 resd 1

section .text
global main
extern printf
extern scanf
extern exit
extern Sleep
extern strcpy
extern GetStdHandle
extern ReadConsoleA
extern CreateProcessA
extern CreateDirectoryA
extern CreateFileA
extern WriteFile
extern CloseHandle
extern sprintf
extern ReadFile
extern DeleteFileA
extern MoveFileA
extern strcat
section .text
main:
push -10
call GetStdHandle
mov [hConsoleInput], eax
mov dword [startup_info], 68
push init_var_user_name
push var_user_name
call strcpy
add esp, 8
push init_var_output_file
push var_output_file
call strcpy
add esp, 8
push init_var_summary
push var_summary
call strcpy
add esp, 8
push str_0
call printf
add esp, 4
push str_1
call printf
add esp, 4
push 0
push chars_read_0
push 255
push var_user_name
push dword [hConsoleInput]
call ReadConsoleA
add esp, 20
mov ecx, [chars_read_0]
mov esi, var_user_name
sub ecx, 2
mov byte [esi + ecx], 0
push str_2
call printf
add esp, 4
push var_user_age
push format_number_input
call scanf
add esp, 8
push 0
call exit
