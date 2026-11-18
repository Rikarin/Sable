" Sable source files, and scripts whose first line runs sable.
autocmd BufNewFile,BufRead *.sb setfiletype sable
autocmd BufNewFile,BufRead * if getline(1) =~# '^#!.*\<sable\>' | setfiletype sable | endif
