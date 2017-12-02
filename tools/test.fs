\ Test input for assembler.

\ Primitive ALU instruction definitions
0x6180 alu: swap          ( a b -- b a )
0x6020 alu: 2dup/!        ( a b -- a b )
0x6381 alu: 2dup/and      ( a b -- a b a&b )
0x6103 alu: drop          ( x -- )
0x6600 alu: invert        ( x -- ~x )
0x6203 alu: +             ( a b -- a+b)
0x6a03 alu: -             ( a b -- a-b)
0x6903 alu: rshift        ( a b -- a>>b )
0x6d03 alu: lshift        ( a b -- a<<b )
0x6303 alu: and           ( a b -- a&b)
0x6403 alu: or            ( a b -- a|b)
0x6081 alu: dup           ( x -- x x )
0x6c00 alu: @             ( x -- [x] )
0x6703 alu: =             ( a b -- a=b )
0x6f03 alu: u<            ( a b -- a<b )
0x6803 alu: <             ( a b -- a<b )
0x6181 alu: over          ( a b -- a b a )
0x6e81 alu: depth         ( a b -- a b a )
0x6147 alu: >r            ( a --  R: -- a )
0x6b8d alu: r>            ( -- a  R: a -- )

4 org

: execute  ( i*x xt -- j*x ) >r ;

\ I/O port addresses are defined as their complements, so they can be loaded by
\ a literal instruction and inverted before use. TODO - the assembler should
\ probably do this for us.
0x7FFF constant ~outport      ( 8000 )
0x7FFD constant ~outport-set  ( 8002 )
0x7FFB constant ~outport-clr  ( 8004 )
0x7FF9 constant ~outport-tog  ( 8006 )
0x5FFF constant ~inport       ( A000 )
0x3FFF constant ~timer-ctr    ( C000 )
0x3FFD constant ~timer-flags  ( C002 )
0x3FFB constant ~timer-m0     ( C004 )
0x3FF9 constant ~timer-m1     ( C006 )
0x0FFF constant ~irqcon-st    ( F000 )
0x0FFD constant ~irqcon-en    ( F002 )
0x0FFB constant ~irqcon-se    ( F004 )
0x0FF9 constant ~irqcon-ce    ( F006 )

variable t0delay-elapsed

\ Delays for u cycles, plus a smidge of overhead, using timer match channel 0.
: t0delay  ( u -- )
  \ Clear flag
  0 t0delay-elapsed !
  \ Read counter
  ~timer-ctr invert @
  \ Compute target M0 value
  +
  \ Set M0
  ~timer-m0 invert !
  \ Clear any existing pending interrupt on M0
  2 ~timer-flags invert !
  \ Enable interrupt
  0x4000 ~irqcon-se invert !
  \ Spin
  begin
    t0delay-elapsed @
  until ;

: t0delay-isr
  \ Set flag
  1 t0delay-elapsed !
  \ Disable interrupt
  0x4000 ~irqcon-ce invert !
  \ Acknowledge interrupt at source
  2 ~timer-flags invert !
  ;

\ For 19200 bps, one bit = 52083.333 ns
\ At 48MHz core clock, 1 cycle = 20.833 ns
\ Thus: 2500.04 cycles / bit
2500 constant cycles/bit
1250 constant cycles/bit/2

variable uart-tx-bits
variable uart-tx-count
: uart-tx-init
  0 uart-tx-count ! ;

: tx-isr
  1 ~timer-flags invert !       \ acknowledge interrupt
  uart-tx-bits @                ( bits )
  1 over and                    ( bits lsb )
  if ~outport-set else ~outport-clr then invert ( bits regaddr )
  1 swap !                      ( bits )
  1 rshift uart-tx-bits !       ( )

  uart-tx-count @  1 -  dup  uart-tx-count !
  if
    ~timer-ctr invert @  cycles/bit +  ~timer-m1 invert !
  else
    \ disable interrupt
    0x2000 ~irqcon-ce invert !
  then ;

: tx
  \ Wait for the transmitter to be free
  begin uart-tx-count @ 0 = until
  \ Frame the byte
  1 lshift              \ add a start bit
  0x200 or              \ add a stop bit
  uart-tx-bits !        \ stash it where the ISR can find it
  10 uart-tx-count !    \ prepare to transmit 10 bits
  \ enable interrupt
  0x2000 ~irqcon-se invert ! ;



: bit-delay cycles/bit t0delay ;
: half-bit-delay cycles/bit/2 t0delay ;

\ Treating 'c' as a shift register, transmits its LSB and shifts it to the
\ right.
: bit>  ( c -- c' )
  1 2dup/and          ( c 1 lsb )
  if ~outport-set else ~outport-clr then invert 2dup/! drop  ( c 1 )
  rshift              ( c' )
  bit-delay ;

\ Transmits a byte with no parity, one stop bit.
: tx  ( c -- )
  1 lshift      \ evacuate start bit
  0x200 or      \ set stop bit
  bit>          \ start bit
  bit> bit> bit> bit>
  bit> bit> bit> bit> \ data bits
  bit>          \ stop bit
  drop ;

\ Samples the status of the RX pin into bit 15.
: rx?  ( -- ? ) ~inport invert @ 15 lshift ;

\ Spins until observing a high-to-low transition on RX.
: ...start
  begin rx?     until
  begin rx? 0 = until ;

: >bit  ( x -- x' )
  1 rshift
  rx? or
  bit-delay ;

: CTSon 2 ~outport-clr invert ! ;
: CTSoff 2 ~outport-set invert ! ;

\ Receives a byte from the RX pin and returns both the bits received, and a success
\ flag. The receive may be unsuccessful if there was a framing error.
\ This manages an outgoing clear-to-send flow control signal on port 0 bit 1. This
\ is unfortunately not quite enough to prevent transmit overruns by an FTDI chip,
\ which can take up to four bytes to acknowledge it.
: rx  ( -- c ? )
  CTSon                     \ Turn on flow control.
  ...start half-bit-delay   \ Delay until halfway into the suspected start bit.
  0 >bit                    \ Record the start bit level.
  0 >bit >bit >bit >bit
    >bit >bit >bit >bit     \ Record the data bits
    8 rshift                \ and shift
  swap >bit  14 rshift      \ Record the stop bit with the start bit and shift.
  CTSoff                    \ Turn off flow control during the stop bit.
  2 = \ Stop bit high, start bit low => binary 10 => 2
  ;

: rx!
  rx if exit then
  rx! ;

\ Simple monitor
: cr 0x0d tx 0x0a tx ;
: space 0x20 tx ;

: .nib  ( c -- c' )
  dup 12 rshift   \ extract top nibble
  9 over u< if 7 + then [char] 0 +   \ convert to hex
  tx
  4 lshift ;

: .hex .nib .nib .nib .nib space drop ;

: >nib  ( x -- x' ? )
  begin
    rx!
    3 over = if drop 0 exit then
    32 over < if
      dup tx
      [char] 0 -  9 over u< if 7 - then
      0xF over u< if
        drop  7 tx  8 tx
      else
        swap 4 lshift or
        1 exit
      then
    else
      drop 7 tx
    then
  again ;

: read-word
  0 >nib if >nib if >nib if >nib if 1 exit then then then then
  drop 0 ;

: dump
  begin
    over over = if drop drop exit then
    dup .hex space
    dup @ .hex cr
    2 +
  again ;

: cmd
  depth .hex [char] > tx space
  rx! dup tx

  [char] r over = if drop
    read-word if
      space @ .hex
    else
      [char] ? tx
    then
    cr exit
  then

  [char] w over = if drop
    read-word if
      space read-word if
        swap ! [char] ! tx
      else
        [char] ? tx
      then
    else
      [char] ? tx
    then
    cr exit
  then

  [char] t over = if drop
    space dup .hex cr exit
  then

  [char] p over = if drop
    read-word if
    else
      [char] ? tx
    then
    cr exit
  then

  [char] d over = if drop
    drop cr exit
  then

  [char] x over = if drop
    read-word if
      cr
      execute
    else
      [char] ? tx cr
    then
    exit
  then

  [char] v over = if drop
    read-word if
      space read-word if
        cr swap dump
      else
        [char] ? tx cr
      then
    else
      [char] ? tx cr
    then
    exit
  then

  drop
  [char] ? tx cr ;

: hello
  [char] H tx
  [char] e tx
  [char] l dup tx tx
  [char] o tx
  [char] ! tx
  cr ;

\ This is intended as an example ISR that shows visible evidence of having run.
: ledtog 0xF0 ~outport-tog invert ! ;

\ Return from an interrupt handler. Must be called from tail position.
: reti
  \ Adjust the return address.
  r> 2 - >r
  \ Re-enable interrupts
  ~irqcon-st invert 2dup/! drop ;

variable isr-count

: generic-isr
  ~irqcon-st invert @
  0x4000 over and if
    t0delay-isr
  then
  0x2000 over and if
    tx-isr
  then
  drop
  reti ;

: chatty
  uart-tx-init
  0 ~irqcon-st invert ! \ Enable interrupts
  CTSoff
  1 bit> drop           \ Ensure TX is high for a bit time before beginning.
  hello
  begin cmd again ;

0 org
: main chatty ;
: vechack generic-isr ;
