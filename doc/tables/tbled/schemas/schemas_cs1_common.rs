&[
    ("effect", &[
        ("id", Type::U8),
        ("data", Type::Repeat(2, Box::new(Type::I16))),
    ]),
    ("QSCookVariantDescription", &[
        ("item_id", Type::I16),
        ("description_1", Type::CUtf8),
        ("description_2", Type::CUtf8),
    ]),
]