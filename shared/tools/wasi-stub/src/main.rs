// wasi-stub: replace `wasi_snapshot_preview1` imports in a wasm module with
// local function stubs. Two modes per import:
//
//   * "trap"  — function body is `unreachable` (canister traps if called)
//   * "noop"  — function body returns zeros for every result type (silent
//                no-op; useful for environ_get/_sizes_get etc. that the
//                .NET runtime calls during _initialize without caring)
//
// The default policy is "noop" because the .NET runtime touches several
// of these imports during reactor _initialize before any canister query
// runs. A "trap" default would make the canister reject every install.
//
// Usage:
//   wasi-stub <input.wasm> <output.wasm> [--trap=name1,name2,...]

use anyhow::{Context, Result};
use std::collections::HashSet;
use walrus::{
    ir::Value,
    FunctionBuilder, FunctionId, Module, ValType,
};

const WASI_MODULE: &str = "wasi_snapshot_preview1";

fn parse_args() -> Result<(String, String, HashSet<String>)> {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 {
        anyhow::bail!("usage: wasi-stub <input.wasm> <output.wasm> [--trap=name,name,...]");
    }
    let mut trap = HashSet::new();
    for arg in &args[3..] {
        if let Some(rest) = arg.strip_prefix("--trap=") {
            for n in rest.split(',') {
                trap.insert(n.to_string());
            }
        } else {
            anyhow::bail!("unknown arg: {arg}");
        }
    }
    Ok((args[1].clone(), args[2].clone(), trap))
}

fn build_stub(module: &mut Module, params: &[ValType], results: &[ValType], trap: bool) -> FunctionId {
    let mut builder = FunctionBuilder::new(&mut module.types, params, results);
    let mut body = builder.func_body();
    if trap {
        body.unreachable();
    } else {
        // Push a zero of the correct type for every result so the function
        // type-checks without doing anything observable.
        for r in results {
            match r {
                ValType::I32 => {
                    body.const_(Value::I32(0));
                }
                ValType::I64 => {
                    body.const_(Value::I64(0));
                }
                ValType::F32 => {
                    body.const_(Value::F32(0.0));
                }
                ValType::F64 => {
                    body.const_(Value::F64(0.0));
                }
                _ => {
                    // Reference types — bail to a trap; noop semantics
                    // aren't meaningful for ref types here.
                    body.unreachable();
                    break;
                }
            }
        }
    }
    let locals = vec![];
    builder.finish(locals, &mut module.funcs)
}

fn main() -> Result<()> {
    let (input, output, trap_set) = parse_args()?;
    let mut module = Module::from_file(&input).with_context(|| format!("read {input}"))?;

    // Collect (import_id, function_id, name, type_id) for every wasi import.
    let mut targets = Vec::new();
    for imp in module.imports.iter() {
        if imp.module == WASI_MODULE {
            if let walrus::ImportKind::Function(fid) = imp.kind {
                let ty_id = module.funcs.get(fid).ty();
                targets.push((imp.id(), fid, imp.name.clone(), ty_id));
            }
        }
    }

    if targets.is_empty() {
        eprintln!("wasi-stub: no {WASI_MODULE} imports found in {input}");
    }

    let mut stubbed = 0usize;
    let mut trapped = 0usize;
    for (import_id, old_fid, name, ty_id) in targets {
        let trap_this = trap_set.contains(&name);
        let (params, results) = {
            let ty = module.types.get(ty_id);
            (ty.params().to_vec(), ty.results().to_vec())
        };
        let stub_fid = build_stub(&mut module, &params, &results, trap_this);
        // Redirect every reference to old_fid → stub_fid.
        replace_func_refs(&mut module, old_fid, stub_fid);
        // Drop the import (its function is now an orphan; walrus keeps it
        // alive only because it's defined; the references go to the stub).
        module.imports.delete(import_id);
        // Also delete the old (now-imported-but-orphaned) function entry.
        module.funcs.delete(old_fid);
        if trap_this {
            trapped += 1;
        } else {
            stubbed += 1;
        }
    }

    module.emit_wasm_file(&output).with_context(|| format!("write {output}"))?;
    eprintln!("wasi-stub: replaced {stubbed} no-op + {trapped} trap stubs → {output}");
    Ok(())
}

// Walk every function body and rewrite Call instructions targeting old_fid
// to call new_fid instead. walrus also stores function references in
// element segments and exports; handle those too.
fn replace_func_refs(module: &mut Module, old: FunctionId, new: FunctionId) {
    use walrus::ir::*;
    struct Replacer {
        old: FunctionId,
        new: FunctionId,
    }
    impl VisitorMut for Replacer {
        fn visit_function_id_mut(&mut self, fid: &mut FunctionId) {
            if *fid == self.old {
                *fid = self.new;
            }
        }
    }
    let mut replacer = Replacer { old, new };

    let local_func_ids: Vec<_> = module
        .funcs
        .iter_local()
        .map(|(id, _)| id)
        .collect();
    for id in local_func_ids {
        let local = module.funcs.get_mut(id).kind.unwrap_local_mut();
        dfs_pre_order_mut(&mut replacer, local, local.entry_block());
    }

    // Element segments may store function refs.
    for elem in module.elements.iter_mut() {
        match &mut elem.items {
            walrus::ElementItems::Functions(fids) => {
                for fid in fids.iter_mut() {
                    if *fid == old {
                        *fid = new;
                    }
                }
            }
            walrus::ElementItems::Expressions(_, _) => {
                // Const expressions referencing function refs — skip;
                // not used by NativeAOT-LLVM output for our case.
            }
        }
    }

    // Exports.
    for exp in module.exports.iter_mut() {
        if let walrus::ExportItem::Function(fid) = &mut exp.item {
            if *fid == old {
                *fid = new;
            }
        }
    }
}
