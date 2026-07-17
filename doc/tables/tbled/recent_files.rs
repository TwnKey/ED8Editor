// cache of most recently used files
//
// Should be updated on every load/read and persisted to disk.
// Only UTF-8 paths are supported.
// The number of files is hardcoded to 10.
//
// Disk format is the concatenation of:
// - u32 (little endian): length of the path
// - UTF-8 encoded path (not null-terminated)
// for every path.
// Paths are sorted by recency of use and

use camino::{Utf8Path, Utf8PathBuf};

const MAX_SIZE: usize = 10;

pub struct RecentFiles {
    /// Queue of paths, sorted from most recent use to least recent use.
    /// Must not contain exact duplicates.
    paths: std::collections::VecDeque<Utf8PathBuf>,
}

impl RecentFiles {
    pub fn new() -> Self {
        Self {
            paths: std::collections::VecDeque::with_capacity(MAX_SIZE),
        }
    }

    pub fn get(&self, i: usize) -> Option<&Utf8Path> {
        self.paths.get(i).map(|path| path.as_ref())
    }

    pub fn iter(&self) -> impl Iterator<Item = &Utf8Path> {
        self.paths.iter().map(|path| path.as_ref())
    }

    pub fn is_empty(&self) -> bool {
        self.paths.is_empty()
    }

    pub fn write_to_file(&self, filename: &Utf8Path) -> std::io::Result<()> {
        let mut data = std::vec::Vec::<u8>::with_capacity(1024);
        for path in self.paths.iter() {
            let bytes = path.as_str().as_bytes();
            let len: u32 = bytes.len().try_into().unwrap();
            data.extend_from_slice(&len.to_le_bytes());
            data.extend_from_slice(bytes);
        }
        std::fs::write(filename, &data)
    }

    fn find_path_index(&self, path: &Utf8Path) -> Option<usize> {
        for (i, p) in self.iter().enumerate() {
            if p == path {
                return Some(i);
            }
        }
        None
    }

    pub fn mark_as_updated(&mut self, path: &Utf8Path) {
        match self.find_path_index(path) {
            None => {
                // path is new, hasn't been used recently
                if self.paths.len() >= MAX_SIZE {
                    self.paths.pop_back();
                }
                self.paths.push_front(path.to_owned());
            }
            Some(i) => {
                if i == 0 {
                    // nothing to do, path was already the most recent one before this call
                } else {
                    // remove path from its current position and move it to the front
                    let p = self.paths.remove(i).unwrap();
                    self.paths.push_front(p);
                }
            }
        }
    }

    pub fn refresh_from_file(&mut self, filename: &Utf8Path) {
        fn inner(this: &mut RecentFiles, filename: &Utf8Path) -> Option<()> {
            let data = std::fs::read(filename).ok()?;
            let mut off = 0;
            while off < data.len() && this.paths.len() < MAX_SIZE {
                let len_bytes = data.get(off..off + 4)?;
                let len_bytes: [u8; 4] = len_bytes.try_into().ok()?;
                let len: usize = u32::from_le_bytes(len_bytes) as usize;
                off += 4;
                let bytes = data.get(off..off + len)?;
                let s = std::str::from_utf8(bytes).ok()?;
                let path = Utf8Path::new(s);
                // Add path if it hasn't appeared before.
                // Note that we can't use the mark_as_updated method because we would reverse the correct order
                if this.find_path_index(path).is_none() {
                    this.paths.push_back(path.to_owned());
                }
                off += len;
            }
            Some(())
        }

        self.paths.clear();
        let result = inner(self, filename);
        if result.is_none() {
            self.paths.clear();
        }
    }
}

struct RecentFilesPaths {
    schemas_v1: Utf8PathBuf,
    schemas_v2: Utf8PathBuf,
    tbls: Utf8PathBuf,
}

fn get_files_paths() -> Option<RecentFilesPaths> {
    let project_dirs = directories::ProjectDirs::from(
        "",                // qualifier
        "huellenoperator", // organization
        "tbled",           // application
    )?;
    let base: &Utf8Path = project_dirs.data_local_dir().try_into().ok()?;
    log::debug!("cache dir: {base}");
    std::fs::create_dir_all(base).ok()?;
    Some(RecentFilesPaths {
        schemas_v1: base.join("recent_schemas_v1.dat"),
        schemas_v2: base.join("recent_schemas_v2.dat"),
        tbls: base.join("recent_tbls.dat"),
    })
}

lazy_static::lazy_static! {
    static ref PATHS: Option<RecentFilesPaths> = get_files_paths();
}

fn load_file(path: Option<&Utf8Path>) -> RecentFiles {
    let mut result = RecentFiles::new();
    if let Some(p) = path {
        result.refresh_from_file(p);
    }
    result
}

pub fn load_recent_schemas_v1() -> RecentFiles {
    load_file(PATHS.as_ref().map(|p| p.schemas_v1.as_ref()))
}

pub fn save_recent_schemas_v1(recent_files: &RecentFiles) -> Option<()> {
    PATHS.as_ref().and_then(|p| recent_files.write_to_file(&p.schemas_v1).ok())
}

pub fn load_recent_schemas_v2() -> RecentFiles {
    load_file(PATHS.as_ref().map(|p| p.schemas_v2.as_ref()))
}

pub fn save_recent_schemas_v2(recent_files: &RecentFiles) -> Option<()> {
    PATHS.as_ref().and_then(|p| recent_files.write_to_file(&p.schemas_v2).ok())
}

pub fn load_recent_tbls() -> RecentFiles {
    load_file(PATHS.as_ref().map(|p| p.tbls.as_ref()))
}

pub fn save_recent_tbls(recent_files: &RecentFiles) -> Option<()> {
    PATHS.as_ref().and_then(|p| recent_files.write_to_file(&p.tbls).ok())
}