// Popup window that allows selecting and reordering visible columns

use tocs::tbl::Attribute;

use fltk::prelude::BrowserExt as _;
use fltk::prelude::GroupExt as _;
use fltk::prelude::WidgetExt as _;
use fltk::prelude::WindowExt as _;

#[derive(Clone, Copy, Debug)]
enum Msg {
    Hide,
    Unhide,
    HideAll,
    UnhideAll,
    MoveUp,
    MoveDown,
    MoveToTop,
    MoveToBottom,
    ShowAll,
    Discard,
    Apply,
}

pub fn select_cols(all: indexmap::IndexSet<Attribute>, visible: &indexmap::IndexSet<Attribute>) -> Option<indexmap::IndexSet<Attribute>> {
    let mut win = fltk::window::Window::default().with_size(640, 360).with_label("select visible attributes/columns");

    let (sender, receiver) = fltk::app::channel();

    let mut browser_visible;
    let mut browser_hidden;

    {
        let mut flex = fltk::group::Flex::default().size_of_parent();
        flex.set_type(fltk::group::FlexType::Column);
        flex.set_pad(5);

        {
            let mut flex_row = fltk::group::Flex::default();
            flex_row.set_type(fltk::group::FlexType::Row);
            flex_row.set_pad(5);

            // left "browser": hidden columns
            {
                let mut flex_col = fltk::group::Flex::default();
                flex_col.set_type(fltk::group::FlexType::Column);
                let label = fltk::frame::Frame::default().with_label("hidden columns");
                flex_col.fixed(&label, 20);
                browser_hidden = fltk::browser::MultiBrowser::default();
                browser_hidden.set_tooltip("Attributes in this list will be hidden.");
                all.difference(visible).for_each(|attr| browser_hidden.add(attr.as_str()));
                flex_col.end();
            }

            // buttons in the middle to move between them
            {
                let mut flex_col = fltk::group::Flex::default();
                flex_col.set_type(fltk::group::FlexType::Column);
                flex_col.set_pad(5);
                let _space = fltk::widget::Widget::default();
                let mut btn_right = fltk::button::Button::default().with_size(25, 25).with_label("@>");
                btn_right.emit(sender, Msg::Unhide);
                btn_right.set_tooltip("Show selected hidden columns.");
                flex_col.fixed(&btn_right, 25);
                let mut btn_left = fltk::button::Button::default().with_size(25, 25).with_label("@<");
                btn_left.emit(sender, Msg::Hide);
                btn_left.set_tooltip("Hide selected visible columns.");
                flex_col.fixed(&btn_left, 25);
                let mut btn_all_right = fltk::button::Button::default().with_size(25, 25).with_label("@>>");
                btn_all_right.emit(sender, Msg::UnhideAll);
                btn_all_right.set_tooltip("Show all columns.");
                flex_col.fixed(&btn_all_right, 25);
                let mut btn_all_left = fltk::button::Button::default().with_size(25, 25).with_label("@<<");
                btn_all_left.emit(sender, Msg::HideAll);
                btn_all_left.set_tooltip("Hide all columns.");
                flex_col.fixed(&btn_all_left, 25);
                let mut btn_up = fltk::button::Button::default().with_size(25, 25).with_label("@8>");
                btn_up.emit(sender, Msg::MoveUp);
                btn_up.set_tooltip("Move selected visible columns one position up.");
                flex_col.fixed(&btn_up, 25);
                let mut btn_down = fltk::button::Button::default().with_size(25, 25).with_label("@2>");
                btn_down.emit(sender, Msg::MoveDown);
                btn_down.set_tooltip("Move selected visible columns one position down.");
                flex_col.fixed(&btn_down, 25);
                let mut btn_top = fltk::button::Button::default().with_size(25, 25).with_label("@8UpArrow");
                btn_top.emit(sender, Msg::MoveToTop);
                btn_top.set_tooltip("Move selected visible columns to the top.");
                flex_col.fixed(&btn_top, 25);
                let mut btn_bottom = fltk::button::Button::default().with_size(25, 25).with_label("@2DnArrow");
                btn_bottom.emit(sender, Msg::MoveToBottom);
                btn_bottom.set_tooltip("Move selected visible columns to the bottom.");
                flex_col.fixed(&btn_bottom, 25);
                let mut btn_show_all = fltk::button::Button::default().with_size(25, 25).with_label("@$reload");
                btn_show_all.emit(sender, Msg::ShowAll);
                btn_show_all.set_tooltip("Show all columns in the order as they appear in the .tbl file.");
                flex_col.fixed(&btn_bottom, 25);
                let _space = fltk::widget::Widget::default();
                flex_col.end();
                flex_row.fixed(&flex_col, 25);
            }

            // right "browser": visible columns
            {
                let mut flex_col = fltk::group::Flex::default();
                flex_col.set_type(fltk::group::FlexType::Column);
                let label = fltk::frame::Frame::default().with_label("visible columns");
                flex_col.fixed(&label, 20);
                browser_visible = fltk::browser::MultiBrowser::default();
                browser_visible.set_tooltip("Attributes in this list will be visible. Their order from top to bottom determines the column order from left to right.");
                visible.iter().for_each(|attr| browser_visible.add(attr.as_str()));
                flex_col.end();
            }

            flex_row.end();
        }

        {
            let mut flex_row = fltk::group::Flex::default();
            let _space = fltk::widget::Widget::default();
            flex_row.set_type(fltk::group::FlexType::Row);
            let mut cancel = fltk::button::Button::default().with_label("Discard changes");
            cancel.emit(sender, Msg::Discard);
            cancel.set_tooltip("Do not change column visibility or order and close this window.");
            flex_row.fixed(&cancel, 140);
            let _space = fltk::widget::Widget::default();
            let mut ok = fltk::button::Button::default().with_label("Apply changes");
            ok.emit(sender, Msg::Apply);
            ok.set_tooltip("Apply changes to the column visibility and order and close this window.");
            flex_row.fixed(&ok, 140);
            let _space = fltk::widget::Widget::default();
            flex_row.end();
            flex.fixed(&flex_row, 26);
        }

        flex.end();
    }

    win.make_modal(true);
    win.make_resizable(true);
    win.end();
    win.show();

    while win.shown() {
        fltk::app::wait();
        let Some(msg) = receiver.recv() else { continue };
        match msg {
            Msg::Discard => {
                win.hide();
                return None;
            }
            Msg::Apply => {
                win.hide();
                let mut visible = indexmap::IndexSet::with_capacity(browser_visible.size().try_into().expect("nonnegative size"));
                for i in 1..=browser_visible.size() {
                    visible.insert(Attribute::from(browser_visible.text(i).expect("1 <= i <= size")));
                }
                return Some(visible);
            }
            Msg::Hide => {
                // move selected columns from visible to hidden
                let mut selected = vec![];
                for i in (1..=browser_visible.size()).rev() {
                    if browser_visible.selected(i) {
                        let text = browser_visible.text(i).expect("selected should have text");
                        selected.push(text);
                        browser_visible.remove(i);
                    }
                }
                // push in reverse order to restore the original order
                for text in selected.iter().rev() {
                    browser_hidden.add(text);
                    browser_hidden.select(browser_hidden.size());
                }
            }
            Msg::Unhide => {
                // move selected columns from hidden to visible
                let mut selected = vec![];
                for i in (1..=browser_hidden.size()).rev() {
                    if browser_hidden.selected(i) {
                        let text = browser_hidden.text(i).expect("selected should have text");
                        selected.push(text);
                        browser_hidden.remove(i);
                    }
                }
                // push in reverse order to restore the original order
                for text in selected.iter().rev() {
                    browser_visible.add(text);
                    browser_visible.select(browser_hidden.size());
                }
            }
            Msg::HideAll => {
                // move all columns from visible to hidden
                for i in 1..=browser_visible.size() {
                    let text = browser_visible.text(i).expect("selected should have text");
                    browser_hidden.add(&text);
                }
                browser_visible.clear();
            }
            Msg::UnhideAll => {
                // move all columns from hidden to visible
                for i in 1..=browser_hidden.size() {
                    let text = browser_hidden.text(i).expect("selected should have text");
                    browser_visible.add(&text);
                }
                browser_hidden.clear();
            }
            Msg::MoveUp => {
                // move selected visible columns one up
                // we start from 2 because we can't move the topmost one up
                for i in 2..=browser_visible.size() {
                    if browser_visible.selected(i) {
                        browser_visible.swap(i, i - 1);
                    }
                }
            }
            Msg::MoveDown => {
                // move selected visible columns one down
                // we stop before size because we can't move the bottommost one down
                for i in (1..browser_visible.size()).rev() {
                    if browser_visible.selected(i) {
                        browser_visible.swap(i, i + 1);
                    }
                }
            }
            Msg::MoveToTop => {
                // move selected visible columns to top
                let mut selected = vec![];
                for i in (1..=browser_visible.size()).rev() {
                    if browser_visible.selected(i) {
                        selected.push(browser_visible.text(i).expect("selected should have text"));
                        browser_visible.remove(i);
                    }
                }
                for t in selected.iter() {
                    browser_visible.insert(1, t);
                }
                for i in 1..=selected.len() {
                    browser_visible.select(i.try_into().unwrap());
                }
                browser_visible.top_line(1);
            }
            Msg::MoveToBottom => {
                // move selected visible columns to bottom
                let mut selected = vec![];
                for i in (1..=browser_visible.size()).rev() {
                    if browser_visible.selected(i) {
                        selected.push(browser_visible.text(i).expect("selected should have text"));
                        browser_visible.remove(i);
                    }
                }
                for t in selected.iter().rev() {
                    browser_visible.add(t);
                }
                for i in 0..selected.len() {
                    browser_visible.select(browser_visible.size() - (i as i32));
                }
                browser_visible.bottom_line(browser_visible.size());
            }
            Msg::ShowAll => {
                // show all columns in schema order
                browser_hidden.clear();
                browser_visible.clear();
                all.iter().for_each(|attr| browser_visible.add(attr.as_str()));
            }
        }
        // make sure we never lose or duplicate columns
        let mut v = indexmap::IndexSet::with_capacity(all.len());
        for i in 1..=browser_hidden.size() {
            v.insert(Attribute::from(browser_hidden.text(i).expect("i < size")));
        }
        for i in 1..=browser_visible.size() {
            v.insert(Attribute::from(browser_visible.text(i).expect("i < size")));
        }
        assert_eq!(all.len(), usize::try_from(browser_hidden.size() + browser_visible.size()).unwrap());
        assert_eq!(v, all);
    }

    None
}