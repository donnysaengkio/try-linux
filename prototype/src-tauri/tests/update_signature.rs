// The installer's update signature checks out against the public key in tauri.conf.json, as the updater checks
// it before installing. TRYLINUX_SETUP=path-to-setup.exe cargo test --test update_signature -- --ignored
use base64::Engine;

#[test]
#[ignore]
fn installer_signature_verifies() {
    let b64 = base64::engine::general_purpose::STANDARD;
    let conf: serde_json::Value = serde_json::from_str(include_str!("../tauri.conf.json")).unwrap();
    let pubkey = String::from_utf8(b64.decode(conf["plugins"]["updater"]["pubkey"].as_str().unwrap()).unwrap()).unwrap();
    let key = minisign_verify::PublicKey::decode(&pubkey).unwrap();
    let setup = std::env::var("TRYLINUX_SETUP").unwrap();
    let sig_b64 = std::fs::read_to_string(format!("{setup}.sig")).unwrap();
    let sig = minisign_verify::Signature::decode(&String::from_utf8(b64.decode(sig_b64.trim()).unwrap()).unwrap()).unwrap();
    let data = std::fs::read(&setup).unwrap();
    key.verify(&data, &sig, false).expect("the signature should match");
    // And a changed installer is refused.
    let mut bad = data.clone();
    bad[1000] ^= 1;
    assert!(key.verify(&bad, &sig, false).is_err());
    println!("verified {} bytes", data.len());
}
