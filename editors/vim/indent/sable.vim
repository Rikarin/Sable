" Sable indentation: braces, brackets and parentheses, one level each, as
" `sable fmt` lays them out. C's indenter does that for a brace language
" without `;`, told not to indent after a line that does not end one.
if exists("b:did_indent")
  finish
endif
let b:did_indent = 1

setlocal cindent
" No extra indent for a statement continued on the next line (there is no
" `;` to end one), nor for labels, case arms or a function's return type.
setlocal cinoptions=+0,(1s,u0,U1,m1,j1,J1,L0,:0,l1,t0
setlocal cinkeys-=0#
setlocal indentkeys-=0#

let b:undo_indent = "setlocal cindent< cinoptions< cinkeys< indentkeys<"
