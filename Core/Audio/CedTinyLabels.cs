// ==================================================
// CedTinyLabels.cs - 楽器の AI（CED-Tiny）の出力の 527 種類の音の名前（AudioSet の名前。出力の位置の順）
//   mispeech/ced-tiny の config.json（id2label・Apache License 2.0）のとおり。名前は AudioSet のオントロジーのまま
//   （Google Inc.・CC BY-SA 4.0 https://creativecommons.org/licenses/by-sa/4.0/ ・ https://github.com/audioset/ontology）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public sealed partial class CedTiny
{
    /// <summary>出力の位置ごとの音の名前（527 個。楽器のまとまりは、番号ではなくこの名前で探す: SceneClasses）</summary>
    public static readonly string[] Labels =
    {
        "Speech",   // 0
        "Male speech",   // 1
        "Female speech",   // 2
        "Child speech",   // 3
        "Conversation",   // 4
        "Narration",   // 5
        "Babbling",   // 6
        "Speech synthesizer",   // 7
        "Shout",   // 8
        "Bellow",   // 9
        "Whoop",   // 10
        "Yell",   // 11
        "Battle cry",   // 12
        "Children shouting",   // 13
        "Screaming",   // 14
        "Whispering",   // 15
        "Laughter",   // 16
        "Baby laughter",   // 17
        "Giggle",   // 18
        "Snicker",   // 19
        "Belly laugh",   // 20
        "Chuckle",   // 21
        "Crying",   // 22
        "Baby cry",   // 23
        "Whimper",   // 24
        "Wail",   // 25
        "Sigh",   // 26
        "Singing",   // 27
        "Choir",   // 28
        "Yodeling",   // 29
        "Chant",   // 30
        "Mantra",   // 31
        "Male singing",   // 32
        "Female singing",   // 33
        "Child singing",   // 34
        "Synthetic singing",   // 35
        "Rapping",   // 36
        "Humming",   // 37
        "Groan",   // 38
        "Grunt",   // 39
        "Whistling",   // 40
        "Breathing",   // 41
        "Wheeze",   // 42
        "Snoring",   // 43
        "Gasp",   // 44
        "Pant",   // 45
        "Snort",   // 46
        "Cough",   // 47
        "Throat clearing",   // 48
        "Sneeze",   // 49
        "Sniff",   // 50
        "Run",   // 51
        "Shuffle",   // 52
        "Walk",   // 53
        "Chewing",   // 54
        "Biting",   // 55
        "Gargling",   // 56
        "Stomach rumble",   // 57
        "Burping",   // 58
        "Hiccup",   // 59
        "Fart",   // 60
        "Hands",   // 61
        "Finger snapping",   // 62
        "Clapping",   // 63
        "Heart sounds",   // 64
        "Heart murmur",   // 65
        "Cheering",   // 66
        "Applause",   // 67
        "Chatter",   // 68
        "Crowd",   // 69
        "Hubbub",   // 70
        "Children playing",   // 71
        "Animal",   // 72
        "Domestic animals",   // 73
        "Dog",   // 74
        "Bark",   // 75
        "Yip",   // 76
        "Howl",   // 77
        "Bow-wow",   // 78
        "Growling",   // 79
        "Whimper (dog)",   // 80
        "Cat",   // 81
        "Purr",   // 82
        "Meow",   // 83
        "Hiss",   // 84
        "Caterwaul",   // 85
        "Livestock",   // 86
        "Horse",   // 87
        "Clip-clop",   // 88
        "Neigh",   // 89
        "Cattle",   // 90
        "Moo",   // 91
        "Cowbell",   // 92
        "Pig",   // 93
        "Oink",   // 94
        "Goat",   // 95
        "Bleat",   // 96
        "Sheep",   // 97
        "Fowl",   // 98
        "Chicken",   // 99
        "Cluck",   // 100
        "Crowing",   // 101
        "Turkey",   // 102
        "Gobble",   // 103
        "Duck",   // 104
        "Quack",   // 105
        "Goose",   // 106
        "Honk",   // 107
        "Wild animals",   // 108
        "Roaring cats (lions",   // 109
        "Roar",   // 110
        "Bird",   // 111
        "Bird vocalization",   // 112
        "Chirp",   // 113
        "Squawk",   // 114
        "Pigeon",   // 115
        "Coo",   // 116
        "Crow",   // 117
        "Caw",   // 118
        "Owl",   // 119
        "Hoot",   // 120
        "Bird flight",   // 121
        "Canidae",   // 122
        "Rodents",   // 123
        "Mouse",   // 124
        "Patter",   // 125
        "Insect",   // 126
        "Cricket",   // 127
        "Mosquito",   // 128
        "Fly",   // 129
        "Buzz",   // 130
        "Bee",   // 131
        "Frog",   // 132
        "Croak",   // 133
        "Snake",   // 134
        "Rattle",   // 135
        "Whale vocalization",   // 136
        "Music",   // 137
        "Musical instrument",   // 138
        "Plucked string instrument",   // 139
        "Guitar",   // 140
        "Electric guitar",   // 141
        "Bass guitar",   // 142
        "Acoustic guitar",   // 143
        "Steel guitar",   // 144
        "Tapping (guitar technique)",   // 145
        "Strum",   // 146
        "Banjo",   // 147
        "Sitar",   // 148
        "Mandolin",   // 149
        "Zither",   // 150
        "Ukulele",   // 151
        "Keyboard (musical)",   // 152
        "Piano",   // 153
        "Electric piano",   // 154
        "Organ",   // 155
        "Electronic organ",   // 156
        "Hammond organ",   // 157
        "Synthesizer",   // 158
        "Sampler",   // 159
        "Harpsichord",   // 160
        "Percussion",   // 161
        "Drum kit",   // 162
        "Drum machine",   // 163
        "Drum",   // 164
        "Snare drum",   // 165
        "Rimshot",   // 166
        "Drum roll",   // 167
        "Bass drum",   // 168
        "Timpani",   // 169
        "Tabla",   // 170
        "Cymbal",   // 171
        "Hi-hat",   // 172
        "Wood block",   // 173
        "Tambourine",   // 174
        "Rattle (instrument)",   // 175
        "Maraca",   // 176
        "Gong",   // 177
        "Tubular bells",   // 178
        "Mallet percussion",   // 179
        "Marimba",   // 180
        "Glockenspiel",   // 181
        "Vibraphone",   // 182
        "Steelpan",   // 183
        "Orchestra",   // 184
        "Brass instrument",   // 185
        "French horn",   // 186
        "Trumpet",   // 187
        "Trombone",   // 188
        "Bowed string instrument",   // 189
        "String section",   // 190
        "Violin",   // 191
        "Pizzicato",   // 192
        "Cello",   // 193
        "Double bass",   // 194
        "Wind instrument",   // 195
        "Flute",   // 196
        "Saxophone",   // 197
        "Clarinet",   // 198
        "Harp",   // 199
        "Bell",   // 200
        "Church bell",   // 201
        "Jingle bell",   // 202
        "Bicycle bell",   // 203
        "Tuning fork",   // 204
        "Chime",   // 205
        "Wind chime",   // 206
        "Change ringing (campanology)",   // 207
        "Harmonica",   // 208
        "Accordion",   // 209
        "Bagpipes",   // 210
        "Didgeridoo",   // 211
        "Shofar",   // 212
        "Theremin",   // 213
        "Singing bowl",   // 214
        "Scratching (performance technique)",   // 215
        "Pop music",   // 216
        "Hip hop music",   // 217
        "Beatboxing",   // 218
        "Rock music",   // 219
        "Heavy metal",   // 220
        "Punk rock",   // 221
        "Grunge",   // 222
        "Progressive rock",   // 223
        "Rock and roll",   // 224
        "Psychedelic rock",   // 225
        "Rhythm and blues",   // 226
        "Soul music",   // 227
        "Reggae",   // 228
        "Country",   // 229
        "Swing music",   // 230
        "Bluegrass",   // 231
        "Funk",   // 232
        "Folk music",   // 233
        "Middle Eastern music",   // 234
        "Jazz",   // 235
        "Disco",   // 236
        "Classical music",   // 237
        "Opera",   // 238
        "Electronic music",   // 239
        "House music",   // 240
        "Techno",   // 241
        "Dubstep",   // 242
        "Drum and bass",   // 243
        "Electronica",   // 244
        "Electronic dance music",   // 245
        "Ambient music",   // 246
        "Trance music",   // 247
        "Music of Latin America",   // 248
        "Salsa music",   // 249
        "Flamenco",   // 250
        "Blues",   // 251
        "Music for children",   // 252
        "New-age music",   // 253
        "Vocal music",   // 254
        "A capella",   // 255
        "Music of Africa",   // 256
        "Afrobeat",   // 257
        "Christian music",   // 258
        "Gospel music",   // 259
        "Music of Asia",   // 260
        "Carnatic music",   // 261
        "Music of Bollywood",   // 262
        "Ska",   // 263
        "Traditional music",   // 264
        "Independent music",   // 265
        "Song",   // 266
        "Background music",   // 267
        "Theme music",   // 268
        "Jingle (music)",   // 269
        "Soundtrack music",   // 270
        "Lullaby",   // 271
        "Video game music",   // 272
        "Christmas music",   // 273
        "Dance music",   // 274
        "Wedding music",   // 275
        "Happy music",   // 276
        "Funny music",   // 277
        "Sad music",   // 278
        "Tender music",   // 279
        "Exciting music",   // 280
        "Angry music",   // 281
        "Scary music",   // 282
        "Wind",   // 283
        "Rustling leaves",   // 284
        "Wind noise (microphone)",   // 285
        "Thunderstorm",   // 286
        "Thunder",   // 287
        "Water",   // 288
        "Rain",   // 289
        "Raindrop",   // 290
        "Rain on surface",   // 291
        "Stream",   // 292
        "Waterfall",   // 293
        "Ocean",   // 294
        "Waves",   // 295
        "Steam",   // 296
        "Gurgling",   // 297
        "Fire",   // 298
        "Crackle",   // 299
        "Vehicle",   // 300
        "Boat",   // 301
        "Sailboat",   // 302
        "Rowboat",   // 303
        "Motorboat",   // 304
        "Ship",   // 305
        "Motor vehicle (road)",   // 306
        "Car",   // 307
        "Vehicle horn",   // 308
        "Toot",   // 309
        "Car alarm",   // 310
        "Power windows",   // 311
        "Skidding",   // 312
        "Tire squeal",   // 313
        "Car passing by",   // 314
        "Race car",   // 315
        "Truck",   // 316
        "Air brake",   // 317
        "Air horn",   // 318
        "Reversing beeps",   // 319
        "Ice cream truck",   // 320
        "Bus",   // 321
        "Emergency vehicle",   // 322
        "Police car (siren)",   // 323
        "Ambulance (siren)",   // 324
        "Fire engine",   // 325
        "Motorcycle",   // 326
        "Traffic noise",   // 327
        "Rail transport",   // 328
        "Train",   // 329
        "Train whistle",   // 330
        "Train horn",   // 331
        "Railroad car",   // 332
        "Train wheels squealing",   // 333
        "Subway",   // 334
        "Aircraft",   // 335
        "Aircraft engine",   // 336
        "Jet engine",   // 337
        "Propeller",   // 338
        "Helicopter",   // 339
        "Fixed-wing aircraft",   // 340
        "Bicycle",   // 341
        "Skateboard",   // 342
        "Engine",   // 343
        "Light engine (high frequency)",   // 344
        "Dental drill",   // 345
        "Lawn mower",   // 346
        "Chainsaw",   // 347
        "Medium engine (mid frequency)",   // 348
        "Heavy engine (low frequency)",   // 349
        "Engine knocking",   // 350
        "Engine starting",   // 351
        "Idling",   // 352
        "Accelerating",   // 353
        "Door",   // 354
        "Doorbell",   // 355
        "Ding-dong",   // 356
        "Sliding door",   // 357
        "Slam",   // 358
        "Knock",   // 359
        "Tap",   // 360
        "Squeak",   // 361
        "Cupboard open or close",   // 362
        "Drawer open or close",   // 363
        "Dishes",   // 364
        "Cutlery",   // 365
        "Chopping (food)",   // 366
        "Frying (food)",   // 367
        "Microwave oven",   // 368
        "Blender",   // 369
        "Water tap",   // 370
        "Sink (filling or washing)",   // 371
        "Bathtub (filling or washing)",   // 372
        "Hair dryer",   // 373
        "Toilet flush",   // 374
        "Toothbrush",   // 375
        "Electric toothbrush",   // 376
        "Vacuum cleaner",   // 377
        "Zipper (clothing)",   // 378
        "Keys jangling",   // 379
        "Coin (dropping)",   // 380
        "Scissors",   // 381
        "Electric shaver",   // 382
        "Shuffling cards",   // 383
        "Typing",   // 384
        "Typewriter",   // 385
        "Computer keyboard",   // 386
        "Writing",   // 387
        "Alarm",   // 388
        "Telephone",   // 389
        "Telephone bell ringing",   // 390
        "Ringtone",   // 391
        "Telephone dialing",   // 392
        "Dial tone",   // 393
        "Busy signal",   // 394
        "Alarm clock",   // 395
        "Siren",   // 396
        "Civil defense siren",   // 397
        "Buzzer",   // 398
        "Smoke detector",   // 399
        "Fire alarm",   // 400
        "Foghorn",   // 401
        "Whistle",   // 402
        "Steam whistle",   // 403
        "Mechanisms",   // 404
        "Ratchet",   // 405
        "Clock",   // 406
        "Tick",   // 407
        "Tick-tock",   // 408
        "Gears",   // 409
        "Pulleys",   // 410
        "Sewing machine",   // 411
        "Mechanical fan",   // 412
        "Air conditioning",   // 413
        "Cash register",   // 414
        "Printer",   // 415
        "Camera",   // 416
        "Single-lens reflex camera",   // 417
        "Tools",   // 418
        "Hammer",   // 419
        "Jackhammer",   // 420
        "Sawing",   // 421
        "Filing (rasp)",   // 422
        "Sanding",   // 423
        "Power tool",   // 424
        "Drill",   // 425
        "Explosion",   // 426
        "Gunshot",   // 427
        "Machine gun",   // 428
        "Fusillade",   // 429
        "Artillery fire",   // 430
        "Cap gun",   // 431
        "Fireworks",   // 432
        "Firecracker",   // 433
        "Burst",   // 434
        "Eruption",   // 435
        "Boom",   // 436
        "Wood",   // 437
        "Chop",   // 438
        "Splinter",   // 439
        "Crack",   // 440
        "Glass",   // 441
        "Chink",   // 442
        "Shatter",   // 443
        "Liquid",   // 444
        "Splash",   // 445
        "Slosh",   // 446
        "Squish",   // 447
        "Drip",   // 448
        "Pour",   // 449
        "Trickle",   // 450
        "Gush",   // 451
        "Fill (with liquid)",   // 452
        "Spray",   // 453
        "Pump (liquid)",   // 454
        "Stir",   // 455
        "Boiling",   // 456
        "Sonar",   // 457
        "Arrow",   // 458
        "Whoosh",   // 459
        "Thump",   // 460
        "Thunk",   // 461
        "Electronic tuner",   // 462
        "Effects unit",   // 463
        "Chorus effect",   // 464
        "Basketball bounce",   // 465
        "Bang",   // 466
        "Slap",   // 467
        "Whack",   // 468
        "Smash",   // 469
        "Breaking",   // 470
        "Bouncing",   // 471
        "Whip",   // 472
        "Flap",   // 473
        "Scratch",   // 474
        "Scrape",   // 475
        "Rub",   // 476
        "Roll",   // 477
        "Crushing",   // 478
        "Crumpling",   // 479
        "Tearing",   // 480
        "Beep",   // 481
        "Ping",   // 482
        "Ding",   // 483
        "Clang",   // 484
        "Squeal",   // 485
        "Creak",   // 486
        "Rustle",   // 487
        "Whir",   // 488
        "Clatter",   // 489
        "Sizzle",   // 490
        "Clicking",   // 491
        "Clickety-clack",   // 492
        "Rumble",   // 493
        "Plop",   // 494
        "Jingle",   // 495
        "Hum",   // 496
        "Zing",   // 497
        "Boing",   // 498
        "Crunch",   // 499
        "Silence",   // 500
        "Sine wave",   // 501
        "Harmonic",   // 502
        "Chirp tone",   // 503
        "Sound effect",   // 504
        "Pulse",   // 505
        "Inside",   // 506
        "Inside",   // 507
        "Inside",   // 508
        "Outside",   // 509
        "Outside",   // 510
        "Reverberation",   // 511
        "Echo",   // 512
        "Noise",   // 513
        "Environmental noise",   // 514
        "Static",   // 515
        "Mains hum",   // 516
        "Distortion",   // 517
        "Sidetone",   // 518
        "Cacophony",   // 519
        "White noise",   // 520
        "Pink noise",   // 521
        "Throbbing",   // 522
        "Vibration",   // 523
        "Television",   // 524
        "Radio",   // 525
        "Field recording",   // 526
    };   // no-loc（AI の音の名前。画面には出さない）
}
