" Sable: what `sable fmt` writes (four spaces, no tabs), and its comments.
if exists("b:did_ftplugin")
  finish
endif
let b:did_ftplugin = 1

setlocal expandtab shiftwidth=4 softtabstop=4 tabstop=4
setlocal commentstring=//\ %s
setlocal comments=s1:/*,mb:*,ex:*/,:///,://
setlocal formatoptions-=t formatoptions+=croqj
setlocal suffixesadd=.sb

let b:undo_ftplugin = "setlocal expandtab< shiftwidth< softtabstop< tabstop< commentstring< comments< formatoptions< suffixesadd<"
