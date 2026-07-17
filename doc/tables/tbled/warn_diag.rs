use fltk::prelude::DisplayExt as _;
use fltk::prelude::GroupExt as _;
use fltk::prelude::WidgetExt as _;
use fltk::prelude::WindowExt as _;

use std::fmt::Write as _;

pub struct WarningDialog {}

impl WarningDialog {
    pub fn new<'a, I: Iterator<Item = &'a tocs::tbl::DeserializeError>>(warnings: I) -> Self {
        let mut window = fltk::window::Window::default();
        window.set_label(&format!("{} {} (warnings)", env!("CARGO_PKG_NAME"), env!("CARGO_PKG_VERSION")));
        window.set_size(1000, 700);

        let mut flex = fltk::group::Flex::default().size_of_parent();
        flex.set_type(fltk::group::FlexType::Column);

        let s = "the following non-fatal error(s) occured loading the file.\nplease study them carefully and, if necessary, fix them before continuing!";
        let mut label = fltk::frame::Frame::default();
        label.set_label(s);
        let h = fltk::draw::measure(s, true).1;
        flex.fixed(&label, h + h / 10);

        let mut s = String::new();
        for (i, w) in warnings.enumerate() {
            writeln!(s, "WARNING #{}: {}", i + 1, w.detail).unwrap();
            writeln!(s, "  - file position: {} (0x{:x})", w.position, w.position).unwrap();
            writeln!(s, "  - context:").unwrap();
            for pos in w.context.iter() {
                writeln!(s, "    - {pos}").unwrap();
            }
            writeln!(s).unwrap();
        }
        let mut buf = fltk::text::TextBuffer::default();
        buf.set_text(&s);
        let mut output = fltk::text::TextDisplay::default();
        output.set_buffer(buf);

        let mut flex_row = fltk::group::Flex::default();
        flex_row.set_type(fltk::group::FlexType::Row);
        let _ = fltk::widget::Widget::default(); // for spacing
        let mut button = fltk::button::Button::default();
        button.set_label("close");
        //flex_row.fixed(&button, 20);
        let _ = fltk::widget::Widget::default(); // for spacing
        flex.fixed(&flex_row, 24);

        flex.end();

        window.make_resizable(true);
        window.make_modal(true);
        window.end();
        window.show();

        button.set_callback(move |_| {
            window.hide();
        });

        Self {}
    }
}