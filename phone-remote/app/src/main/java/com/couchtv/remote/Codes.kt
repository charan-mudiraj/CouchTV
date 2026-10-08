package com.couchtv.remote

/**
 * The CouchTV phone remote's codes: NEC address 0xCE. They match the defaults in CouchTV's remote.ini
 * (see ir-receiver/PROTOCOL.md). 0x60 to 0xFF are free for your own buttons.
 */
object Codes {
    const val ADDRESS = 0xCE

    const val UP = 0x01
    const val DOWN = 0x02
    const val LEFT = 0x03
    const val RIGHT = 0x04
    const val OK = 0x05
    const val BACK = 0x06          // hold for Home
    const val HOME = 0x07
    const val MENU = 0x08
    const val EXIT = 0x09
    const val PLAY_PAUSE = 0x0A
    const val DELETE = 0x0B

    const val VOLUME_UP = 0x10
    const val VOLUME_DOWN = 0x11
    const val MUTE = 0x12

    const val POWER = 0x20          // sleep, and wake the PC
    const val SLEEP_TIMER = 0x21

    const val POINTER_UP = 0x30
    const val POINTER_DOWN = 0x31
    const val POINTER_LEFT = 0x32
    const val POINTER_RIGHT = 0x33
    const val CLICK = 0x34
    const val RIGHT_CLICK = 0x35
    const val SCROLL_UP = 0x36
    const val SCROLL_DOWN = 0x37

    const val NETFLIX = 0x40
    const val YOUTUBE = 0x41
    const val PRIME_VIDEO = 0x42
    const val JIOHOTSTAR = 0x43
    const val WEB = 0x44

    const val FULL_SCREEN = 0x50

    // Voice search (see "Text" in ir-receiver/PROTOCOL.md)
    const val SEARCH = 0x51          // the phone is listening: the TV opens search and turns the sound down
    const val TEXT = 0x52            // text frames follow
    const val SEARCH_CANCEL = 0x53   // stopped listening without any words

    /** Commands for your own buttons. */
    val FREE = 0x60..0xFF
}
