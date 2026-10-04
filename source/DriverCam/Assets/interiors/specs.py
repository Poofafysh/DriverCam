"""Per-car interior specs: a rough take on each real car's cabin layout (from public reference of the real cars).

Game car -> real car (identified from the body meshes; low-confidence ones can be corrected here):
  Rotary    Mazda RX-7 (FD)            Shadow  Lamborghini Countach     Bond      Aston Martin DB5
  Phoenix   Pontiac Firebird (3rd gen) Centaur Ford Mustang fastback '69 Delivery  DeLorean DMC-12
  Saber     Nissan Skyline GT-R (R32)  Centipede Porsche 911 Turbo (930) Justice  Chevrolet Corvette (C3)
  Vektor    Vector W8

Gauge cells (gauges.py atlas): 0 speedo, 1 tach, 2 fuel, 3 temp, 4 oil, 5 volts, 6 clock, 7 digital panel.
Gauges: (cell, dx, dy, r) in metres on the cluster plane (dx right, dy up) around the cluster centre.
Colours: (r, g, b, smoothness, metallic, glow). glow scales DriverCam's InteriorBrightness self-light.
pal["headliner"]: the roof lining and pillar trims (group Trim); defaults to the door colour.
trim: the cabin's greenhouse (group Trim), merged over TRIM_DEFAULT:
  rear      "2+2" (rear seats, quarter windows, C-pillars, parcel shelf), "hatch" (two seats, cargo deck, the roof
            runs on to a big rear hatch glass) or "bulkhead" (mid-engine / two-seater: a wall right behind the seats
            with a small framed rear window; the headliner ends at it)
  frameless door glass without a frame (no window-frame strip up the B-pillar)
  b_w       B-pillar width along the car (m); quarter: rear quarter window length (2+2 / hatch)
  sill      widest door-top cap (m, card face to the window line)
  slope     rear window angle from horizontal (degrees)
  pedals    2 (automatic) or 3
"""

def C(r, g, b, s=0.25, m=0.0, glow=0.35): return (r, g, b, s, m, glow)

BLACK_PLASTIC = C(0.045, 0.045, 0.05, 0.3)
DARK_GREY = C(0.09, 0.09, 0.1, 0.28)
CARPET = C(0.035, 0.035, 0.038, 0.05)
CHROME = C(0.75, 0.76, 0.78, 0.85, 1.0, 0.25)
ALU = C(0.55, 0.56, 0.58, 0.55, 0.9, 0.25)
RUBBER = C(0.03, 0.03, 0.03, 0.15)

# Trim defaults (each car overrides what differs)
TRIM_DEFAULT = dict(rear="2+2", frameless=False, b_w=0.10, quarter=0.40, slope=32, pedals=3, sill=0.15)

# The Part.Interior offsets the shipped setups had for the layout-1 auto-fit cockpits (the same table as
# CarPresets.OldInterior in DriverCam): a migrated setup (Part.Interior identity) is built at this old dash position.
OLD_INTERIOR = {
    "Bond": (-0.0088, 0.1546, 0.2691, 0, -3.03, 0, 0.86), "Centaur": (-0.0293, 0.1279, 0.39, 0, -1.81, 0, 0.88),
    "Centipede": (0.0462, 0.1808, 0.5221, 0, -2.39, 0, 0.86), "Delivery": (0.0205, 0.1557, 0.4437, 0, -2.17, 0, 0.84),
    "Justice": (-0.0032, 0.1367, 0.5766, 0, -2.38, 0, 0.785), "Phoenix": (0.0101, 0.1619, 0.3822, 0, -2.75, 0, 0.84),
    "Rotary": (0.017, 0.2234, 0.1503, 0, -2.62, 0, 0.82), "Saber": (0.0177, 0.1079, 0.2901, 0, 0.05, 0, 0.88),
    "Shadow": (-0.0548, 0.2136, 0.2302, 0, -0.16, 0, 0.82), "Vektor": (0.0248, 0.1737, 0.0916, 0, -1.18, 0, 0.84),
}

SPECS = {
    "Rotary": dict(real="Mazda RX-7 (FD)",
        pal=dict(headliner=C(0.24, 0.24, 0.25, 0.08), dash=BLACK_PLASTIC, dash_top=C(0.06, 0.06, 0.065, 0.2), seat=C(0.05, 0.05, 0.055, 0.2), seat_insert=C(0.16, 0.16, 0.17, 0.1),
                 carpet=CARPET, console=DARK_GREY, bezel=C(0.12, 0.12, 0.13, 0.5, 0.3), chrome=ALU, wheel=C(0.04, 0.04, 0.04, 0.35), knob=RUBBER),
        dash=dict(depth=0.42, rise=0.03, wrap=0.10), binnacle="hood", cluster_w=0.36,
        gauges=[(1, 0.0, 0.0, 0.068), (0, 0.135, -0.01, 0.052), (2, -0.12, 0.022, 0.03), (3, -0.12, -0.04, 0.03)],
        stack=dict(angle=12, vents="rect2", radio=True, knobs=3, gauges=[]), console=dict(w=0.26, h=0.30, high=False),
        shifter=dict(x="center", style="short", chrome=False), seats="bucket", rear=False,
        trim=dict(rear="hatch", frameless=True, quarter=0.0, slope=24, b_w=0.16),
        wheel=dict(r=0.185, t=0.017, spokes=[0, 180, 270], spoke_w=0.05, hub_r=0.065, wood=False)),
    "Shadow": dict(real="Lamborghini Countach",
        pal=dict(headliner=C(0.42, 0.3, 0.19, 0.15), dash=C(0.05, 0.045, 0.04, 0.25), dash_top=C(0.04, 0.035, 0.03, 0.15), seat=C(0.5, 0.33, 0.18, 0.3), seat_insert=C(0.45, 0.29, 0.15, 0.25),
                 carpet=C(0.2, 0.13, 0.07, 0.05), console=C(0.48, 0.31, 0.17, 0.3), bezel=C(0.1, 0.1, 0.1, 0.4), chrome=CHROME, wheel=C(0.05, 0.04, 0.035, 0.35), knob=C(0.05, 0.05, 0.05, 0.5)),
        dash=dict(depth=0.38, rise=0.0, wrap=0.0), binnacle="box", cluster_w=0.44,
        gauges=[(0, -0.08, 0.0, 0.058), (1, 0.08, 0.0, 0.058), (4, -0.18, 0.025, 0.026), (3, -0.18, -0.035, 0.026), (2, 0.18, 0.025, 0.026), (5, 0.18, -0.035, 0.026)],
        stack=dict(angle=0, vents="rect2", radio=False, knobs=4, gauges=[]), console=dict(w=0.34, h=0.46, high=True),
        shifter=dict(x="center", style="gated", chrome=True), seats="low", rear=False,
        trim=dict(rear="bulkhead", frameless=True, b_w=0.14, sill=0.22),
        wheel=dict(r=0.19, t=0.018, spokes=[0, 180, 270], spoke_w=0.045, hub_r=0.06, wood=False)),
    "Bond": dict(real="Aston Martin DB5",
        pal=dict(headliner=C(0.36, 0.33, 0.28, 0.06), dash=C(0.035, 0.035, 0.035, 0.2), dash_top=C(0.03, 0.03, 0.03, 0.15), seat=C(0.42, 0.07, 0.05, 0.35), seat_insert=C(0.38, 0.06, 0.045, 0.3),
                 carpet=C(0.12, 0.03, 0.025, 0.05), console=C(0.035, 0.035, 0.035, 0.2), bezel=CHROME, chrome=CHROME, wheel=C(0.32, 0.17, 0.07, 0.6), knob=C(0.04, 0.04, 0.04, 0.6),
                 wood=C(0.32, 0.17, 0.07, 0.6)),
        dash=dict(depth=0.30, rise=0.0, wrap=0.0), binnacle="box", cluster_w=0.34,
        gauges=[(0, -0.075, 0.0, 0.058), (1, 0.075, 0.0, 0.058)],
        stack=dict(angle=0, vents="none", radio=True, knobs=0, gauges=[(4, -0.12, 0.0, 0.026), (3, -0.04, 0.0, 0.026), (5, 0.04, 0.0, 0.026), (2, 0.12, 0.0, 0.026)], toggles=6),
        console=dict(w=0.22, h=0.26, high=False),
        shifter=dict(x="center", style="tall", chrome=True), seats="classic", rear=True,
        trim=dict(rear="2+2", quarter=0.34, slope=34),
        wheel=dict(r=0.21, t=0.013, spokes=[0, 180, 270], spoke_w=0.012, hub_r=0.045, wood=True, alu_spokes=True)),
    "Phoenix": dict(real="Pontiac Firebird Trans Am (3rd gen)",
        pal=dict(headliner=C(0.11, 0.11, 0.12, 0.06), dash=C(0.06, 0.06, 0.065, 0.25), dash_top=C(0.05, 0.05, 0.055, 0.2), seat=C(0.1, 0.1, 0.11, 0.1), seat_insert=C(0.2, 0.2, 0.21, 0.05),
                 carpet=CARPET, console=DARK_GREY, bezel=C(0.1, 0.1, 0.11, 0.4), chrome=ALU, wheel=C(0.05, 0.05, 0.05, 0.3), knob=RUBBER),
        dash=dict(depth=0.40, rise=0.02, wrap=0.04), binnacle="box", cluster_w=0.40,
        gauges=[(0, -0.075, 0.005, 0.055), (1, 0.075, 0.005, 0.055), (2, -0.165, 0.02, 0.025), (3, -0.165, -0.035, 0.025), (4, 0.165, 0.02, 0.025), (5, 0.165, -0.035, 0.025)],
        stack=dict(angle=8, vents="rect2", radio=True, knobs=3, gauges=[]), console=dict(w=0.26, h=0.30, high=False),
        shifter=dict(x="center", style="short", chrome=False), seats="bucket", rear=True,
        trim=dict(rear="2+2", frameless=True, quarter=0.30, slope=22),
        wheel=dict(r=0.19, t=0.018, spokes=[30, 150, 210, 330], spoke_w=0.04, hub_r=0.06, wood=False)),
    "Centaur": dict(real="Ford Mustang fastback ('69)",
        pal=dict(headliner=C(0.06, 0.06, 0.065, 0.3), dash=C(0.04, 0.04, 0.045, 0.3), dash_top=C(0.035, 0.035, 0.04, 0.25), seat=C(0.045, 0.045, 0.05, 0.4), seat_insert=C(0.06, 0.06, 0.065, 0.3),
                 carpet=CARPET, console=C(0.045, 0.045, 0.05, 0.35), bezel=CHROME, chrome=CHROME, wheel=C(0.3, 0.16, 0.07, 0.6), knob=C(0.05, 0.05, 0.05, 0.6),
                 wood=C(0.3, 0.16, 0.07, 0.55)),
        dash=dict(depth=0.36, rise=0.0, wrap=0.0, twin_cowl=True), binnacle="pods", cluster_w=0.42,
        gauges=[(0, -0.085, 0.0, 0.055), (1, 0.085, 0.0, 0.055), (2, -0.185, -0.01, 0.03), (3, 0.185, -0.01, 0.03)],
        stack=dict(angle=0, vents="none", radio=True, knobs=2, gauges=[]), console=dict(w=0.24, h=0.30, high=False, wood=True),
        shifter=dict(x="center", style="tall", chrome=True), seats="classic", rear=True,
        trim=dict(rear="2+2", frameless=True, quarter=0.26, slope=22),
        wheel=dict(r=0.20, t=0.016, spokes=[0, 180, 270], spoke_w=0.02, hub_r=0.055, wood=True, alu_spokes=True)),
    "Delivery": dict(real="DeLorean DMC-12",
        pal=dict(headliner=C(0.26, 0.26, 0.27, 0.08), dash=C(0.05, 0.05, 0.055, 0.25), dash_top=C(0.045, 0.045, 0.05, 0.2), seat=C(0.3, 0.3, 0.31, 0.3), seat_insert=C(0.27, 0.27, 0.28, 0.25),
                 carpet=C(0.08, 0.08, 0.085, 0.05), console=C(0.06, 0.06, 0.065, 0.3), bezel=ALU, chrome=ALU, wheel=C(0.05, 0.05, 0.05, 0.35), knob=RUBBER),
        dash=dict(depth=0.40, rise=0.03, wrap=0.05), binnacle="box", cluster_w=0.36,
        gauges=[(0, -0.075, 0.0, 0.055), (1, 0.075, 0.0, 0.055), (2, -0.15, -0.03, 0.022), (3, 0.15, -0.03, 0.022)],
        stack=dict(angle=18, vents="rect2", radio=True, knobs=3, gauges=[]), console=dict(w=0.30, h=0.40, high=True),
        shifter=dict(x="center", style="short", chrome=False), seats="bucket", rear=False,
        trim=dict(rear="bulkhead", b_w=0.12, pedals=3),
        wheel=dict(r=0.19, t=0.018, spokes=[20, 160, 270], spoke_w=0.035, hub_r=0.065, wood=False)),
    "Saber": dict(real="Nissan Skyline GT-R (R32)",
        pal=dict(headliner=C(0.3, 0.3, 0.31, 0.06), dash=C(0.06, 0.06, 0.065, 0.25), dash_top=C(0.055, 0.055, 0.06, 0.2), seat=C(0.08, 0.08, 0.085, 0.1), seat_insert=C(0.22, 0.22, 0.24, 0.05),
                 carpet=CARPET, console=DARK_GREY, bezel=C(0.1, 0.1, 0.11, 0.4), chrome=ALU, wheel=C(0.04, 0.04, 0.04, 0.35), knob=RUBBER),
        dash=dict(depth=0.40, rise=0.02, wrap=0.04), binnacle="box", cluster_w=0.38,
        gauges=[(0, -0.08, 0.0, 0.056), (1, 0.08, 0.0, 0.056), (3, -0.165, -0.02, 0.024), (2, 0.165, -0.02, 0.024)],
        stack=dict(angle=10, vents="rect2", radio=True, knobs=3, gauges=[(4, -0.07, 0.0, 0.022), (5, 0.0, 0.0, 0.022), (3, 0.07, 0.0, 0.022)]),
        console=dict(w=0.26, h=0.30, high=False),
        shifter=dict(x="center", style="short", chrome=False), seats="bucket", rear=True,
        trim=dict(rear="2+2", frameless=True, quarter=0.36, slope=30),
        wheel=dict(r=0.185, t=0.018, spokes=[30, 150, 210, 330], spoke_w=0.035, hub_r=0.06, wood=False)),
    "Centipede": dict(real="Porsche 911 Turbo (930)",
        pal=dict(headliner=C(0.38, 0.33, 0.26, 0.06), dash=C(0.035, 0.035, 0.035, 0.3), dash_top=C(0.03, 0.03, 0.03, 0.25), seat=C(0.4, 0.24, 0.12, 0.35), seat_insert=C(0.36, 0.21, 0.1, 0.3),
                 carpet=C(0.15, 0.09, 0.05, 0.05), console=C(0.035, 0.035, 0.035, 0.3), bezel=C(0.08, 0.08, 0.08, 0.5, 0.2), chrome=ALU, wheel=C(0.04, 0.04, 0.04, 0.35), knob=C(0.04, 0.04, 0.04, 0.5)),
        dash=dict(depth=0.32, rise=0.0, wrap=0.0), binnacle="row", cluster_w=0.56,
        gauges=[(4, -0.22, -0.005, 0.042), (2, -0.115, 0.0, 0.048), (1, 0.0, 0.006, 0.062), (0, 0.115, 0.0, 0.048), (6, 0.22, -0.005, 0.042)],
        stack=dict(angle=0, vents="rect2", radio=True, knobs=2, gauges=[]), console=dict(w=0.20, h=0.22, high=False),
        shifter=dict(x="center", style="tall", chrome=False), seats="bucket", rear=True,
        trim=dict(rear="2+2", quarter=0.38, slope=26),
        wheel=dict(r=0.19, t=0.02, spokes=[0, 180, 240, 300], spoke_w=0.03, hub_r=0.07, wood=False)),
    "Justice": dict(real="Chevrolet Corvette (C3)",
        pal=dict(headliner=C(0.22, 0.035, 0.035, 0.15), dash=C(0.32, 0.04, 0.04, 0.3), dash_top=C(0.26, 0.03, 0.03, 0.2), seat=C(0.36, 0.05, 0.05, 0.35), seat_insert=C(0.33, 0.045, 0.045, 0.3),
                 carpet=C(0.2, 0.03, 0.03, 0.05), console=C(0.3, 0.04, 0.04, 0.3), bezel=CHROME, chrome=CHROME, wheel=C(0.05, 0.05, 0.05, 0.4), knob=C(0.05, 0.05, 0.05, 0.6)),
        dash=dict(depth=0.36, rise=0.01, wrap=0.0, center_arch=True), binnacle="pods", cluster_w=0.34,
        gauges=[(0, -0.08, 0.0, 0.058), (1, 0.08, 0.0, 0.058)],
        stack=dict(angle=0, vents="round2", radio=True, knobs=2, gauges=[(4, 0.0, 0.07, 0.022), (3, 0.0, 0.02, 0.022), (2, 0.0, -0.03, 0.022), (5, 0.0, -0.08, 0.022)]),
        console=dict(w=0.30, h=0.42, high=True),
        shifter=dict(x="center", style="tall", chrome=True), seats="classic", rear=False,
        trim=dict(rear="bulkhead", frameless=True, b_w=0.12, pedals=2),
        wheel=dict(r=0.20, t=0.016, spokes=[30, 150, 210, 330], spoke_w=0.03, hub_r=0.055, wood=False)),
    "Vektor": dict(real="Vector W8",
        pal=dict(headliner=C(0.16, 0.16, 0.17, 0.06), dash=C(0.045, 0.045, 0.05, 0.3), dash_top=C(0.04, 0.04, 0.045, 0.25), seat=C(0.35, 0.05, 0.05, 0.35), seat_insert=C(0.06, 0.06, 0.065, 0.2),
                 carpet=CARPET, console=C(0.07, 0.07, 0.075, 0.4), bezel=C(0.12, 0.12, 0.13, 0.5, 0.3), chrome=ALU, wheel=C(0.04, 0.04, 0.04, 0.35), knob=RUBBER),
        dash=dict(depth=0.38, rise=0.02, wrap=0.08), binnacle="digital", cluster_w=0.42,
        gauges=[(7, 0.0, 0.0, 0.11)],
        stack=dict(angle=14, vents="rect2", radio=False, knobs=4, gauges=[]), console=dict(w=0.36, h=0.42, high=True),
        shifter=dict(x="left", style="short", chrome=False), seats="bucket", rear=False,
        trim=dict(rear="bulkhead", frameless=True, b_w=0.16),
        wheel=dict(r=0.18, t=0.019, spokes=[30, 150, 270], spoke_w=0.04, hub_r=0.06, wood=False)),
}

# ------------------------------------------------------------------ working gauges (one table for the faces and the needles)
# gauges.py draws the faces from it (system Python + PIL) and build_interior.py writes the matching needle lines (Blender's
# Python), so the printed scale and the needle's range can never drift apart.
#   kmh / mph: (dial max, labelled step) in the game HUD's units (the HUD shows real speed x 1.1); one face per unit
#   tach: (dial max, red line), both x1000 rpm
# Rule: dial max >= about 1.2 x the highest speed the HUD shows (about 200-220 km/h, 125-140 mph with cards).
SWEEP = (225, -45)            # big dials: maths angles (counter-clockwise from 3 o'clock) of the low and high ends
DIALS = {
    "Rotary":    dict(style="jdm",      kmh=(280, 40), mph=(180, 20), tach=(9, 8)),
    "Shadow":    dict(style="italian",  kmh=(320, 40), mph=(200, 40), tach=(10, 8)),
    "Bond":      dict(style="classic",  kmh=(280, 40), mph=(160, 20), tach=(7, 5.5)),
    "Phoenix":   dict(style="eighties", kmh=(240, 40), mph=(160, 20), tach=(6, 5)),
    "Centaur":   dict(style="american", kmh=(280, 40), mph=(160, 20), tach=(8, 6)),
    "Delivery":  dict(style="eighties", kmh=(280, 40), mph=(160, 20), tach=(7, 5.5)),
    "Saber":     dict(style="jdm",      kmh=(280, 40), mph=(180, 20), tach=(9, 8)),
    "Centipede": dict(style="german",   kmh=(300, 50), mph=(180, 20), tach=(8, 6.8)),
    "Justice":   dict(style="american", kmh=(280, 40), mph=(160, 20), tach=(7, 5.5)),
    "Vektor":    dict(style="digital",  kmh=(320, 40), mph=(200, 40), tach=(9, 7)),   # digital: 3 digits + 18 rpm bars
}

def minor_ticks(step):
    """Minor divisions between two labels: a tick every 10 (20 -> 2, 40 -> 4, 50 -> 5)."""
    return step // 10 if step % 10 == 0 and 2 <= step // 10 <= 5 else 2

# needle colour per face style (the needles are geometry now; the faces only print the scale)
NEEDLE = {"classic": (0.94, 0.94, 0.94), "jdm": (1.0, 0.27, 0.16), "italian": (1.0, 1.0, 1.0), "american": (1.0, 0.47, 0.12),
          "german": (1.0, 0.47, 0.0), "eighties": (1.0, 0.31, 0.16), "digital": (0.31, 1.0, 0.55)}
NEEDLE_MAT = lambda style: C(*NEEDLE[style], s=0.4, m=0.0, glow=1.0)

# Digital panel (the W8): 11 glyphs (0-9, 10 = blank) in a strip along the top of atlas row 0, and the bar texels
# (unlit, lit, red-lit: solid patches) in cell 6. Pixels in the 1024 x 512 atlas, y down. The quads' UVs are baked on
# glyph 0 / the unlit patch; DriverCam shifts them by k * du (glyphs) or by the lit / red-lit offset (bars).
GLYPH = dict(x0=4, y0=4, w=60, h=96, pitch=64, count=11)
BAR_TEXELS = dict(x0=520, y0=272, w=72, h=224, pitch=80)   # unlit, lit, red-lit patches side by side in cell 6
# panel layout in normalised panel coordinates (x -1..1 left to right, y -1..1 bottom to top of the visible screen)
DIGITAL = dict(digits=3, digit_x0=-0.86, digit_y0=-0.02, digit_h=0.78, digit_gap=1.1,   # glyph height as share of the half height
               unit_x=-0.06, unit_y=0.2,                                                 # unit label (km/h | mph), on the face
               bars=18, bar_x0=-0.86, bar_x1=0.86, bar_y0=-0.82, bar_h0=0.10, bar_h1=0.55, bar_fill=0.62)
