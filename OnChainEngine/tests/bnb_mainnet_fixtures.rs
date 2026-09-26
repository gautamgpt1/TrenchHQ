use serde::Deserialize;
use trenchhq_onchain_engine::protocol::{
    pancake_infinity_bin, pancake_infinity_cl, pancake_v3, uniswap_v2,
};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Fixture {
    collected_at_utc: String,
    source_url: String,
    receipt_source_url: String,
    v2: V2Fixture,
    v3: V3Fixture,
    infinity_cl: InfinityClFixture,
    infinity_bin: InfinityBinFixture,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct V2Fixture {
    chain_id: String,
    factory: String,
    pool: String,
    token0: String,
    token1: String,
    block_number: String,
    block_hash: String,
    parent_hash: String,
    timestamp: String,
    transaction_hash: String,
    transaction_status: String,
    logs: Vec<RawLog>,
    get_reserves: String,
    expected_last_trade_quote_per_base_coefficient: String,
    expected_spot_quote_per_base_coefficient: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct V3Fixture {
    chain_id: String,
    factory: String,
    pool: String,
    token0: String,
    token1: String,
    fee: u32,
    tick_spacing: i32,
    block_number: String,
    block_hash: String,
    parent_hash: String,
    timestamp: String,
    transaction_hash: String,
    transaction_status: String,
    swap: RawLog,
    slot0: String,
    liquidity: String,
    expected_last_trade_quote_per_base_coefficient: String,
    expected_spot_quote_per_base_coefficient: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct InfinityClFixture {
    manager: String,
    pool_id: String,
    currency0: String,
    currency1: String,
    hooks: String,
    fee: u32,
    tick_spacing: i32,
    block_number: String,
    block_hash: String,
    transaction_hash: String,
    swap: RawLog,
    slot0: String,
    liquidity: String,
    expected_last_trade_quote_per_base_coefficient: String,
    expected_spot_quote_per_base_coefficient: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct InfinityBinFixture {
    manager: String,
    pool_id: String,
    currency0: String,
    currency1: String,
    hooks: String,
    fee: u32,
    bin_step: u16,
    bounded_swap_log_count: usize,
    slot0: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawLog {
    topics: Vec<String>,
    data: String,
    transaction_index: String,
    log_index: String,
}

fn fixture() -> Fixture {
    serde_json::from_str(include_str!("fixtures/bnb-mainnet-pancake-20260905.json"))
        .expect("BNB mainnet fixture must remain valid JSON")
}

#[test]
fn pancake_v2_bnb_swap_sync_and_pinned_reserves_match() {
    let fixture = fixture();
    assert!(!fixture.collected_at_utc.is_empty());
    assert_eq!(fixture.source_url, "https://bsc-rpc.publicnode.com");
    assert_eq!(
        fixture.receipt_source_url,
        "https://bsc-dataseed.bnbchain.org"
    );
    let v2 = fixture.v2;
    assert_common_provenance(
        &v2.chain_id,
        &v2.pool,
        &v2.token0,
        &v2.token1,
        &v2.block_number,
        &v2.block_hash,
        &v2.parent_hash,
        &v2.timestamp,
        &v2.transaction_hash,
        &v2.transaction_status,
    );
    assert_eq!(v2.factory, "0xca143ce32fe78f1f7019d7d551a6402fc5350c73");
    let sync_log = v2
        .logs
        .iter()
        .find(|log| log.topics[0].eq_ignore_ascii_case(uniswap_v2::SYNC_TOPIC))
        .expect("fixture must contain the pair Sync log");
    let swap_log = v2
        .logs
        .iter()
        .find(|log| log.topics[0].eq_ignore_ascii_case(uniswap_v2::SWAP_TOPIC))
        .expect("fixture must contain the pair Swap log");
    assert_eq!(sync_log.transaction_index, swap_log.transaction_index);
    assert_ne!(sync_log.log_index, swap_log.log_index);
    let sync = uniswap_v2::decode_sync(&sync_log.data).unwrap();
    let reserves = uniswap_v2::decode_reserves(&v2.get_reserves).unwrap();
    assert_eq!(sync, reserves);
    assert_eq!(
        uniswap_v2::spot_price(&reserves, false, 18, 18)
            .unwrap()
            .coefficient,
        v2.expected_spot_quote_per_base_coefficient
    );
    let swap = uniswap_v2::decode_swap(&swap_log.topics, &swap_log.data).unwrap();
    assert_eq!(
        uniswap_v2::executed_price(&swap, false, 18, 18)
            .unwrap()
            .coefficient,
        v2.expected_last_trade_quote_per_base_coefficient
    );
}

#[test]
fn pancake_v3_bnb_extended_swap_and_pinned_state_match() {
    let v3 = fixture().v3;
    assert_common_provenance(
        &v3.chain_id,
        &v3.pool,
        &v3.token0,
        &v3.token1,
        &v3.block_number,
        &v3.block_hash,
        &v3.parent_hash,
        &v3.timestamp,
        &v3.transaction_hash,
        &v3.transaction_status,
    );
    assert_eq!(v3.factory, pancake_v3::BNB_FACTORY);
    assert_eq!((v3.fee, v3.tick_spacing), (100, 1));
    assert!(v3.swap.transaction_index.starts_with("0x"));
    assert!(v3.swap.log_index.starts_with("0x"));
    let swap = pancake_v3::decode_swap(&v3.swap.topics, &v3.swap.data).unwrap();
    let slot0 = pancake_v3::decode_slot0(&v3.slot0).unwrap();
    let liquidity = pancake_v3::decode_liquidity(&v3.liquidity).unwrap();
    assert_eq!(swap.sqrt_price_x96, slot0.sqrt_price_x96);
    assert_eq!(swap.tick, slot0.tick);
    assert_eq!(swap.liquidity, liquidity);
    assert_eq!(
        pancake_v3::executed_price(&swap, false, 18, 18)
            .unwrap()
            .coefficient,
        v3.expected_last_trade_quote_per_base_coefficient
    );
    assert_eq!(
        pancake_v3::spot_price(&slot0.sqrt_price_x96, false, 18, 18)
            .unwrap()
            .coefficient,
        v3.expected_spot_quote_per_base_coefficient
    );
}

#[test]
fn pancake_infinity_cl_bnb_live_swap_and_state_match() {
    let cl = fixture().infinity_cl;
    assert_eq!(cl.manager, pancake_infinity_cl::BNB_POOL_MANAGER);
    assert_eq!(cl.swap.topics[1], cl.pool_id);
    for address in [&cl.currency0, &cl.currency1, &cl.hooks] {
        assert!(uniswap_v2::is_address(address));
    }
    assert_eq!((cl.fee, cl.tick_spacing), (1, 1));
    assert!(cl.block_number.starts_with("0x"));
    assert_eq!(cl.block_hash.len(), 66);
    assert_eq!(cl.transaction_hash.len(), 66);
    let swap = pancake_infinity_cl::decode_swap(&cl.swap.topics, &cl.swap.data).unwrap();
    let slot0 = pancake_infinity_cl::decode_slot0(&cl.slot0).unwrap();
    let liquidity = pancake_infinity_cl::decode_liquidity(&cl.liquidity).unwrap();
    assert_eq!(swap.sqrt_price_x96, slot0.sqrt_price_x96);
    assert_eq!(swap.tick, slot0.tick);
    assert_eq!(swap.liquidity, liquidity);
    assert_eq!(
        pancake_infinity_cl::executed_price(&swap, true, 18, 18)
            .unwrap()
            .coefficient,
        cl.expected_last_trade_quote_per_base_coefficient
    );
    assert_eq!(
        pancake_infinity_cl::spot_price(&slot0.sqrt_price_x96, true, 18, 18)
            .unwrap()
            .coefficient,
        cl.expected_spot_quote_per_base_coefficient
    );
}

#[test]
fn pancake_infinity_bin_bnb_live_slot0_is_explicitly_state_only() {
    let bin = fixture().infinity_bin;
    assert_eq!(bin.manager, pancake_infinity_bin::BNB_POOL_MANAGER);
    assert_eq!(bin.pool_id.len(), 66);
    for address in [&bin.currency0, &bin.currency1, &bin.hooks] {
        assert!(uniswap_v2::is_address(address));
    }
    assert_eq!(bin.fee, 4);
    assert_eq!(bin.bounded_swap_log_count, 0);
    let slot0 = pancake_infinity_bin::decode_slot0(&bin.slot0).unwrap();
    assert_eq!(slot0.active_id, 8_388_610);
    assert_eq!((slot0.protocol_fee, slot0.lp_fee), (4_097, 4));
    assert_eq!(
        pancake_infinity_bin::spot_price(slot0.active_id, bin.bin_step, true, 18, 18)
            .unwrap()
            .coefficient,
        "1000200010000000000"
    );
}

#[allow(clippy::too_many_arguments)]
fn assert_common_provenance(
    chain_id: &str,
    pool: &str,
    token0: &str,
    token1: &str,
    block_number: &str,
    block_hash: &str,
    parent_hash: &str,
    timestamp: &str,
    transaction_hash: &str,
    transaction_status: &str,
) {
    assert_eq!(chain_id, "56");
    for address in [pool, token0, token1] {
        assert!(uniswap_v2::is_address(address));
    }
    assert!(block_number.starts_with("0x"));
    assert_eq!(block_hash.len(), 66);
    assert_eq!(parent_hash.len(), 66);
    assert!(timestamp.starts_with("0x"));
    assert_eq!(transaction_hash.len(), 66);
    assert_eq!(transaction_status, "0x1");
}
