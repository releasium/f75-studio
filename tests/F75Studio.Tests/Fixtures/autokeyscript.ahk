#Requires AutoHotkey v2.0
#SingleInstance Force

; Basic Vim-style navigation
CapsLock & h::Send "{Blind}{Left}"
CapsLock & j::Send "{Blind}{Down}"
CapsLock & k::Send "{Blind}{Up}"
CapsLock & l::Send "{Blind}{Right}"

; Word navigation
CapsLock & b::Send "{Blind}^{Left}"
CapsLock & w::Send "{Blind}^{Right}"

; Start and end of line
CapsLock & u::Send "{Blind}{Home}"
CapsLock & o::Send "{Blind}{End}"

; Page navigation
CapsLock & y::Send "{Blind}{PgUp}"
CapsLock & p::Send "{Blind}{PgDn}"

; Prevent Caps Lock from toggling when pressed alone
CapsLock::Return

; Caps + Space toggles normal Caps Lock
CapsLock & Space::
{
    SetCapsLockState GetKeyState("CapsLock", "T") ? "Off" : "On"
}