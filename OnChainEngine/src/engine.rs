use crate::domain::{RecoveryState, WatchedPoolSelection};
use crate::evm::domain::{FinalityUpdate, HeadUpdate, LogUpdate, Rollback, SnapshotResponse};
use crate::evm::market_state::EvmMarketState;
use crate::ipc::{
    read_envelope, write_envelope, Envelope, FrameError, MAX_FRAME_BYTES, PROTOCOL_NAME,
    PROTOCOL_VERSION,
};
use crate::market_state::{
    MarketState, MANIFEST_PROTOCOL_ID, METEORA_DAMM_V1_PROTOCOL_ID, METEORA_DAMM_V2_PROTOCOL_ID,
    METEORA_DLMM_PROTOCOL_ID, ORCA_WHIRLPOOL_PROTOCOL_ID, PUMP_PROTOCOL_ID, PUMP_SWAP_PROTOCOL_ID,
    RAYDIUM_AMM_V4_PROTOCOL_ID, RAYDIUM_CLMM_PROTOCOL_ID, RAYDIUM_CPMM_PROTOCOL_ID,
};
use crate::protocol::{
    aerodrome_classic, aerodrome_slipstream, curve, fermi_swap, pancake_infinity_bin,
    pancake_infinity_cl, pancake_v2, pancake_v3, pons_v2_curve, uniswap_v2, uniswap_v3, uniswap_v4,
};
use crate::protocol::{
    manifest, meteora_damm_v1, meteora_damm_v2, meteora_dlmm, orca_whirlpool, pump, pump_swap,
    raydium_amm_v4, raydium_clmm, raydium_cpmm,
};
use crate::raw::{RawAccountUpdate, RawTransactionUpdate};
use base64::engine::general_purpose::STANDARD as BASE64;
use base64::Engine as _;
use serde::Deserialize;
use serde_json::{json, Value};
use std::io::{Read, Write};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum EngineError {
    #[error(transparent)]
    Frame(#[from] FrameError),
    #[error("client sent {message_type} before completing the hello handshake")]
    HandshakeRequired { message_type: String },
    #[error("client protocol {protocol} version {version} is incompatible")]
    IncompatibleProtocol { protocol: String, version: u32 },
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ReplaceWatchedPoolsRequest {
    pools: Vec<WatchedPoolSelection>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RecoveryStateRequest {
    state: RecoveryState,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct DecodePoolAccountRequest {
    protocol_id: String,
    pool_address: String,
    selected_mint: String,
    owner_program: String,
    data_base64: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct DerivePumpBondingCurveRequest {
    mint: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ReferencePriceRequest {
    reference_id: String,
    #[serde(default)]
    chain_id: Option<String>,
    #[serde(default)]
    asset_address: Option<String>,
    value: crate::domain::DecimalValue,
    source_id: String,
    observed_at_unix_ms: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct EvmProviderContextRequest {
    chain_id: String,
    provider_profile_id: String,
    source_id: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct EvmChainRequest {
    chain_id: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct EvmRecoveryStateRequest {
    chain_id: String,
    state: RecoveryState,
}

pub fn run<R: Read, W: Write>(mut input: R, mut output: W) -> Result<(), EngineError> {
    let mut handshake_complete = false;
    let mut state = MarketState::default();
    let mut evm_state = EvmMarketState::default();

    while let Some(request) = read_envelope(&mut input)? {
        if request.protocol != PROTOCOL_NAME || request.protocol_version != PROTOCOL_VERSION {
            let response = Envelope::response(
                "fatalError",
                request.request_id,
                json!({
                    "code": "incompatibleProtocol",
                    "expectedProtocol": PROTOCOL_NAME,
                    "expectedVersion": PROTOCOL_VERSION
                }),
            );
            write_envelope(&mut output, &response)?;
            return Err(EngineError::IncompatibleProtocol {
                protocol: request.protocol,
                version: request.protocol_version,
            });
        }

        if !handshake_complete && request.message_type != "hello" {
            let response = Envelope::response(
                "fatalError",
                request.request_id,
                json!({ "code": "handshakeRequired" }),
            );
            write_envelope(&mut output, &response)?;
            return Err(EngineError::HandshakeRequired {
                message_type: request.message_type,
            });
        }

        let request_id = request.request_id.clone();
        match request.message_type.as_str() {
            "hello" => {
                handshake_complete = true;
                write_envelope(
                    &mut output,
                    &Envelope::response(
                        "helloAck",
                        request_id,
                        json!({
                            "engineVersion": env!("CARGO_PKG_VERSION"),
                            "maxFrameBytes": MAX_FRAME_BYTES,
                            "oneSharedProcess": true,
                            "providerTransport": "managedApp",
                            "chains": ["solana", "eip155"],
                            "protocols": [
                                PUMP_PROTOCOL_ID,
                                PUMP_SWAP_PROTOCOL_ID,
                                RAYDIUM_AMM_V4_PROTOCOL_ID,
                                RAYDIUM_CPMM_PROTOCOL_ID,
                                RAYDIUM_CLMM_PROTOCOL_ID,
                                METEORA_DAMM_V1_PROTOCOL_ID,
                                METEORA_DAMM_V2_PROTOCOL_ID,
                                METEORA_DLMM_PROTOCOL_ID,
                                ORCA_WHIRLPOOL_PROTOCOL_ID,
                                MANIFEST_PROTOCOL_ID,
                                uniswap_v2::PROTOCOL_ID,
                                uniswap_v3::PROTOCOL_ID,
                                uniswap_v4::PROTOCOL_ID,
                                pancake_v2::PROTOCOL_ID,
                                pancake_v3::PROTOCOL_ID,
                                pancake_infinity_cl::PROTOCOL_ID,
                                pancake_infinity_bin::PROTOCOL_ID,
                                aerodrome_classic::PROTOCOL_ID,
                                aerodrome_slipstream::PROTOCOL_ID,
                                curve::PROTOCOL_ID,
                                fermi_swap::PROTOCOL_ID,
                                pons_v2_curve::PROTOCOL_ID
                            ],
                            "features": [
                                "framedJson",
                                "exactDecimal",
                                "accountStateDecode",
                                "transactionEventDecode",
                                "boundedDeduplication",
                                "spotPriceMath",
                                "referencePriceConversion",
                                "snapshotReset",
                                "chainNeutralPoolIdentity",
                                "evmEnvelopeContracts",
                                "uniswapV2SwapAndReservePricing",
                                "uniswapV3SwapAndSqrtPricePricing"
                            ]
                        }),
                    ),
                )?;
            }
            "decodePoolAccount" => {
                match parse_payload::<DecodePoolAccountRequest>(request.payload)
                    .and_then(decode_pool_account)
                {
                    Ok(decoded) => write_envelope(
                        &mut output,
                        &Envelope::response("decodedPoolAccount", request_id, decoded),
                    )?,
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "derivePumpBondingCurve" => {
                match parse_payload::<DerivePumpBondingCurveRequest>(request.payload).and_then(
                    |payload| {
                        pump::derive_bonding_curve(&payload.mint).map_err(|error| error.to_string())
                    },
                ) {
                    Ok((address, bump)) => write_envelope(
                        &mut output,
                        &Envelope::response(
                            "derivedPumpBondingCurve",
                            request_id,
                            json!({ "address": address, "bump": bump }),
                        ),
                    )?,
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "replaceWatchedPools" => {
                match parse_payload::<ReplaceWatchedPoolsRequest>(request.payload).and_then(
                    |payload| {
                        let (solana_pools, evm_pools): (Vec<_>, Vec<_>) =
                            payload.pools.into_iter().partition(|pool| {
                                pool.descriptor.pool_key.deployment_key.chain_namespace != "eip155"
                            });
                        MarketState::validate_watched_pools(&solana_pools)
                            .map_err(|error| error.to_string())?;
                        EvmMarketState::validate_watched_pools(&evm_pools)
                            .map_err(|error| error.to_string())?;
                        state
                            .replace_watched_pools(solana_pools)
                            .map_err(|error| error.to_string())?;
                        let evm_count = evm_state
                            .replace_watched_pools(evm_pools)
                            .map_err(|error| error.to_string())?;
                        Ok(state.watched_pool_count() + evm_count)
                    },
                ) {
                    Ok(count) => write_envelope(
                        &mut output,
                        &Envelope::response(
                            "requestSucceeded",
                            request_id,
                            json!({ "watchedPoolCount": count }),
                        ),
                    )?,
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "referencePriceUpdate" => {
                match parse_payload::<ReferencePriceRequest>(request.payload).and_then(|payload| {
                    match payload.reference_id.as_str() {
                        "solUsd" => state
                            .apply_sol_usd_reference(
                                payload.value,
                                payload.source_id,
                                payload.observed_at_unix_ms,
                            )
                            .map_err(|error| error.to_string()),
                        _ => {
                            let chain_id = payload.chain_id.ok_or_else(|| {
                                format!("reference {} requires a chain ID", payload.reference_id)
                            })?;
                            let definition = crate::evm::chain::by_chain_id(&chain_id)
                                .ok_or_else(|| format!("unsupported EVM chain {chain_id}"))?;
                            if payload.reference_id == "robinhoodStockUsd" {
                                if chain_id != "4663" {
                                    return Err(
                                        "Robinhood Stock Token references require chain 4663"
                                            .to_owned(),
                                    );
                                }
                                let asset_address = payload.asset_address.ok_or_else(|| {
                                    "Robinhood Stock Token reference requires an asset address"
                                        .to_owned()
                                })?;
                                return evm_state
                                    .apply_asset_usd_reference(
                                        &chain_id,
                                        asset_address,
                                        payload.value,
                                        payload.source_id,
                                        payload.observed_at_unix_ms,
                                    )
                                    .map_err(|error| error.to_string());
                            }
                            if payload.reference_id != definition.reference_price_id {
                                return Err(format!(
                                    "reference {} does not match EVM chain {chain_id}",
                                    payload.reference_id
                                ));
                            }
                            evm_state
                                .apply_native_usd_reference(
                                    &chain_id,
                                    payload.value,
                                    payload.source_id,
                                    payload.observed_at_unix_ms,
                                )
                                .map_err(|error| error.to_string())
                        }
                    }
                }) {
                    Ok(updates) => {
                        write_price_updates(&mut output, updates)?;
                        write_optional_success(&mut output, request_id)?;
                    }
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "setRecoveryState" => match parse_payload::<RecoveryStateRequest>(request.payload) {
                Ok(payload) => {
                    state.set_recovery_state(payload.state);
                    write_optional_success(&mut output, request_id)?;
                }
                Err(error) => write_failure(&mut output, request_id, &error)?,
            },
            "resetForSnapshot" => {
                state.reset_for_snapshot();
                write_optional_success(&mut output, request_id)?;
            }
            "resetEvmForSnapshot" => {
                match parse_payload::<EvmChainRequest>(request.payload).and_then(|payload| {
                    evm_state
                        .reset_for_snapshot(&payload.chain_id)
                        .map_err(|error| error.to_string())
                }) {
                    Ok(()) => write_optional_success(&mut output, request_id)?,
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "setEvmRecoveryState" => {
                match parse_payload::<EvmRecoveryStateRequest>(request.payload).and_then(
                    |payload| {
                        evm_state
                            .set_recovery_state(&payload.chain_id, payload.state)
                            .map_err(|error| error.to_string())
                    },
                ) {
                    Ok(()) => write_optional_success(&mut output, request_id)?,
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "setEvmProviderContext" => {
                match parse_payload::<EvmProviderContextRequest>(request.payload).and_then(
                    |context| {
                        evm_state
                            .set_provider_context(
                                &context.chain_id,
                                context.provider_profile_id,
                                context.source_id,
                            )
                            .map_err(|error| error.to_string())
                    },
                ) {
                    Ok(()) => write_optional_success(&mut output, request_id)?,
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "rawAccountUpdate" => {
                match parse_payload::<RawAccountUpdate>(request.payload).and_then(|update| {
                    state
                        .apply_account_update(update)
                        .map_err(|error| error.to_string())
                }) {
                    Ok(updates) => {
                        write_price_updates(&mut output, updates)?;
                        write_optional_success(&mut output, request_id)?;
                    }
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "rawAccountSnapshot" => {
                match parse_payload::<Vec<RawAccountUpdate>>(request.payload).and_then(|updates| {
                    state
                        .apply_account_snapshot(updates)
                        .map_err(|error| error.to_string())
                }) {
                    Ok(updates) => {
                        write_price_updates(&mut output, updates)?;
                        write_optional_success(&mut output, request_id)?;
                    }
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "rawTransactionUpdate" => {
                match parse_payload::<RawTransactionUpdate>(request.payload).and_then(|update| {
                    state
                        .apply_transaction_update(update)
                        .map_err(|error| error.to_string())
                }) {
                    Ok(updates) => {
                        write_price_updates(&mut output, updates)?;
                        write_optional_success(&mut output, request_id)?;
                    }
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "evmHead" => match parse_payload::<HeadUpdate>(request.payload).and_then(|update| {
                evm_state
                    .apply_head(update)
                    .map_err(|error| error.to_string())
            }) {
                Ok(()) => write_optional_success(&mut output, request_id)?,
                Err(error) => write_failure(&mut output, request_id, &error)?,
            },
            "evmLog" => match parse_payload::<LogUpdate>(request.payload).and_then(|update| {
                evm_state
                    .apply_log(update)
                    .map_err(|error| error.to_string())
            }) {
                Ok(updates) => {
                    write_price_updates(&mut output, updates)?;
                    write_optional_success(&mut output, request_id)?;
                }
                Err(error) => write_failure(&mut output, request_id, &error)?,
            },
            "evmSnapshot" => {
                match parse_payload::<SnapshotResponse>(request.payload).and_then(|snapshot| {
                    evm_state
                        .apply_snapshot(snapshot)
                        .map_err(|error| error.to_string())
                }) {
                    Ok(updates) => {
                        write_price_updates(&mut output, updates)?;
                        write_optional_success(&mut output, request_id)?;
                    }
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "evmFinality" => {
                match parse_payload::<FinalityUpdate>(request.payload).and_then(|update| {
                    evm_state
                        .apply_finality(update)
                        .map_err(|error| error.to_string())
                }) {
                    Ok(updates) => {
                        write_price_updates(&mut output, updates)?;
                        write_optional_success(&mut output, request_id)?;
                    }
                    Err(error) => write_failure(&mut output, request_id, &error)?,
                }
            }
            "evmRollback" => match parse_payload::<Rollback>(request.payload).and_then(|rollback| {
                evm_state
                    .apply_rollback(rollback)
                    .map_err(|error| error.to_string())
            }) {
                Ok(updates) => {
                    write_price_updates(&mut output, updates)?;
                    write_optional_success(&mut output, request_id)?;
                }
                Err(error) => write_failure(&mut output, request_id, &error)?,
            },
            "requestSnapshots" => {
                let mut updates = state.snapshots();
                updates.extend(evm_state.snapshots());
                write_envelope(
                    &mut output,
                    &Envelope::response("poolSnapshots", request_id, json!({ "updates": updates })),
                )?;
            }
            "health" => write_envelope(
                &mut output,
                &Envelope::response(
                    "healthSnapshot",
                    request_id,
                    json!({
                        "state": state.recovery_state(),
                        "providerTransport": "managedApp",
                        "watchedPoolCount": state.watched_pool_count() + evm_state.watched_pool_count(),
                        "stale": matches!(state.recovery_state(), RecoveryState::Stale)
                    }),
                ),
            )?,
            "shutdown" => {
                write_envelope(
                    &mut output,
                    &Envelope::response("shutdownAck", request_id, json!({ "stopped": true })),
                )?;
                return Ok(());
            }
            _ => write_envelope(
                &mut output,
                &Envelope::response(
                    "requestFailed",
                    request_id,
                    json!({
                        "code": "unsupportedMessage",
                        "retryable": false
                    }),
                ),
            )?,
        }
    }

    Ok(())
}

fn parse_payload<T: for<'de> Deserialize<'de>>(payload: Value) -> Result<T, String> {
    serde_json::from_value(payload).map_err(|error| format!("invalid request payload: {error}"))
}

fn decode_pool_account(request: DecodePoolAccountRequest) -> Result<Value, String> {
    let data = BASE64
        .decode(request.data_base64.as_bytes())
        .map_err(|_| "invalid base64 account data".to_owned())?;

    match request.protocol_id.as_str() {
        PUMP_PROTOCOL_ID => {
            let state = pump::decode_bonding_curve(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            Ok(json!({
                "protocolId": PUMP_PROTOCOL_ID,
                "poolType": "bondingCurve",
                "poolAddress": request.pool_address,
                "programId": pump::PROGRAM_ID,
                "baseMint": request.selected_mint,
                "quoteMint": state.quote_mint,
                "baseVault": null,
                "quoteVault": null,
                "complete": state.complete
            }))
        }
        PUMP_SWAP_PROTOCOL_ID => {
            let state = pump_swap::decode_pool(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            let layout = match state.layout {
                pump_swap::PoolLayout::Legacy => "legacy",
                pump_swap::PoolLayout::VirtualQuoteReserves => "virtualQuoteReserves",
            };
            Ok(json!({
                "protocolId": PUMP_SWAP_PROTOCOL_ID,
                "poolType": "constantProduct",
                "poolAddress": request.pool_address,
                "programId": pump_swap::PROGRAM_ID,
                "baseMint": state.base_mint,
                "quoteMint": state.quote_mint,
                "baseVault": state.pool_base_token_account,
                "quoteVault": state.pool_quote_token_account,
                "layout": layout,
                "virtualQuoteReserves": state.virtual_quote_reserves.to_string()
            }))
        }
        RAYDIUM_AMM_V4_PROTOCOL_ID => {
            let state = raydium_amm_v4::decode_pool(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            Ok(json!({
                "protocolId": RAYDIUM_AMM_V4_PROTOCOL_ID,
                "poolType": "constantProduct",
                "poolAddress": request.pool_address,
                "programId": raydium_amm_v4::PROGRAM_ID,
                "baseMint": state.coin_mint,
                "quoteMint": state.pc_mint,
                "baseVault": state.coin_vault,
                "quoteVault": state.pc_vault,
                "layout": "ammInfoV4"
            }))
        }
        RAYDIUM_CPMM_PROTOCOL_ID => {
            let state = raydium_cpmm::decode_pool(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            Ok(json!({
                "protocolId": RAYDIUM_CPMM_PROTOCOL_ID,
                "poolType": "constantProduct",
                "poolAddress": request.pool_address,
                "programId": raydium_cpmm::PROGRAM_ID,
                "baseMint": state.token_0_mint,
                "quoteMint": state.token_1_mint,
                "baseVault": state.token_0_vault,
                "quoteVault": state.token_1_vault,
                "layout": "poolStateV1"
            }))
        }
        RAYDIUM_CLMM_PROTOCOL_ID => {
            let state = raydium_clmm::decode_pool(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            Ok(json!({
                "protocolId": RAYDIUM_CLMM_PROTOCOL_ID,
                "poolType": "concentratedLiquidity",
                "poolAddress": request.pool_address,
                "programId": raydium_clmm::PROGRAM_ID,
                "baseMint": state.token_0_mint,
                "quoteMint": state.token_1_mint,
                "baseVault": state.token_0_vault,
                "quoteVault": state.token_1_vault,
                "layout": "poolStateV1"
            }))
        }
        METEORA_DAMM_V1_PROTOCOL_ID => {
            let state = meteora_damm_v1::decode_pool(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            let layout = match state.curve_type {
                meteora_damm_v1::CurveType::ConstantProduct => "constantProduct",
                meteora_damm_v1::CurveType::Stable => "stable",
            };
            Ok(json!({
                "protocolId": METEORA_DAMM_V1_PROTOCOL_ID,
                "poolType": "dynamicAmm",
                "poolAddress": request.pool_address,
                "programId": meteora_damm_v1::PROGRAM_ID,
                "baseMint": state.token_a_mint,
                "quoteMint": state.token_b_mint,
                "baseVault": null,
                "quoteVault": null,
                "enabled": state.enabled,
                "layout": layout,
                "protocolAccounts": [
                    { "role": "tokenAVaultState", "address": state.a_vault },
                    { "role": "tokenBVaultState", "address": state.b_vault },
                    { "role": "tokenAVaultLp", "address": state.a_vault_lp },
                    { "role": "tokenBVaultLp", "address": state.b_vault_lp }
                ]
            }))
        }
        METEORA_DAMM_V2_PROTOCOL_ID => {
            let state = meteora_damm_v2::decode_pool(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            Ok(json!({
                "protocolId": METEORA_DAMM_V2_PROTOCOL_ID,
                "poolType": "concentratedOrCompoundingLiquidity",
                "poolAddress": request.pool_address,
                "programId": meteora_damm_v2::PROGRAM_ID,
                "baseMint": state.token_a_mint,
                "quoteMint": state.token_b_mint,
                "baseVault": state.token_a_vault,
                "quoteVault": state.token_b_vault,
                "layout": "poolV1"
            }))
        }
        METEORA_DLMM_PROTOCOL_ID => {
            let state = meteora_dlmm::decode_pool(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            Ok(json!({
                "protocolId": METEORA_DLMM_PROTOCOL_ID,
                "poolType": "discreteLiquidity",
                "poolAddress": request.pool_address,
                "programId": meteora_dlmm::PROGRAM_ID,
                "baseMint": state.token_x_mint,
                "quoteMint": state.token_y_mint,
                "baseVault": state.reserve_x,
                "quoteVault": state.reserve_y,
                "layout": "lbPairV1"
            }))
        }
        ORCA_WHIRLPOOL_PROTOCOL_ID => {
            let state = orca_whirlpool::decode_pool(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            Ok(json!({
                "protocolId": ORCA_WHIRLPOOL_PROTOCOL_ID,
                "poolType": "concentratedLiquidity",
                "poolAddress": request.pool_address,
                "programId": request.owner_program,
                "baseMint": state.token_mint_a,
                "quoteMint": state.token_mint_b,
                "baseVault": state.token_vault_a,
                "quoteVault": state.token_vault_b,
                "layout": "whirlpoolV1"
            }))
        }
        MANIFEST_PROTOCOL_ID => {
            let state = manifest::decode_market(&request.owner_program, &data)
                .map_err(|error| error.to_string())?;
            Ok(json!({
                "protocolId": MANIFEST_PROTOCOL_ID,
                "poolType": "centralLimitOrderBook",
                "poolAddress": request.pool_address,
                "programId": manifest::PROGRAM_ID,
                "baseMint": state.base_mint,
                "quoteMint": state.quote_mint,
                "baseVault": state.base_vault,
                "quoteVault": state.quote_vault,
                "layout": "marketV0"
            }))
        }
        protocol => Err(format!("unsupported protocol {protocol}")),
    }
}

fn write_price_updates<W: Write>(
    output: &mut W,
    updates: Vec<crate::domain::PriceUpdate>,
) -> Result<(), FrameError> {
    for update in updates {
        write_envelope(
            output,
            &Envelope::event("priceDelta", Some(update.sequence), json!(update)),
        )?;
    }
    Ok(())
}

fn write_optional_success<W: Write>(
    output: &mut W,
    request_id: Option<String>,
) -> Result<(), FrameError> {
    if request_id.is_some() {
        write_envelope(
            output,
            &Envelope::response("requestSucceeded", request_id, json!({})),
        )?;
    }
    Ok(())
}

fn write_failure<W: Write>(
    output: &mut W,
    request_id: Option<String>,
    message: &str,
) -> Result<(), FrameError> {
    write_envelope(
        output,
        &Envelope::response(
            "requestFailed",
            request_id,
            json!({
                "code": "invalidRequest",
                "message": message,
                "retryable": false
            }),
        ),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::domain::{Commitment, DeploymentKey, PoolDescriptor, PoolKey, SupportStatus};
    use crate::ipc::{read_envelope, write_envelope, Envelope};
    use serde_json::json;
    use std::io::Cursor;

    #[test]
    fn hello_and_shutdown_complete_a_versioned_session() {
        let mut input = Vec::new();
        write_envelope(&mut input, &Envelope::request("hello", "1", json!({}))).unwrap();
        write_envelope(&mut input, &Envelope::request("shutdown", "2", json!({}))).unwrap();

        let mut output = Vec::new();
        run(Cursor::new(input), &mut output).unwrap();

        let mut responses = Cursor::new(output);
        let hello = read_envelope(&mut responses).unwrap().unwrap();
        let shutdown = read_envelope(&mut responses).unwrap().unwrap();
        assert_eq!(hello.message_type, "helloAck");
        assert_eq!(hello.payload["oneSharedProcess"], true);
        assert_eq!(hello.payload["providerTransport"], "managedApp");
        assert_eq!(hello.payload["chains"], json!(["solana", "eip155"]));
        assert_eq!(hello.protocol_version, 2);
        assert_eq!(shutdown.message_type, "shutdownAck");
    }

    #[test]
    fn version_one_client_fails_closed() {
        let mut request = Envelope::request("hello", "1", json!({}));
        request.protocol_version = 1;
        let mut input = Vec::new();
        write_envelope(&mut input, &request).unwrap();

        let mut output = Vec::new();
        let error = run(Cursor::new(input), &mut output).unwrap_err();
        assert!(matches!(error, EngineError::IncompatibleProtocol { .. }));

        let response = read_envelope(&mut Cursor::new(output)).unwrap().unwrap();
        assert_eq!(response.message_type, "fatalError");
        assert_eq!(response.payload["code"], "incompatibleProtocol");
        assert_eq!(response.payload["expectedVersion"], 2);
    }

    #[test]
    fn credentials_or_other_messages_cannot_precede_hello() {
        let mut input = Vec::new();
        write_envelope(
            &mut input,
            &Envelope::request("configureProvider", "1", json!({ "credential": "secret" })),
        )
        .unwrap();

        let mut output = Vec::new();
        let error = run(Cursor::new(input), &mut output).unwrap_err();
        assert!(matches!(error, EngineError::HandshakeRequired { .. }));

        let response = read_envelope(&mut Cursor::new(output)).unwrap().unwrap();
        assert_eq!(response.message_type, "fatalError");
        assert_eq!(response.payload["code"], "handshakeRequired");
    }

    #[test]
    fn transaction_event_emits_a_price_delta_through_the_framed_protocol() {
        let mint_bytes = [3_u8; 32];
        let mint = bs58::encode(mint_bytes).into_string();
        let descriptor = PoolDescriptor {
            pool_key: PoolKey {
                deployment_key: DeploymentKey {
                    chain_namespace: "solana".to_owned(),
                    chain_id: "mainnet-beta".to_owned(),
                    protocol_id: PUMP_PROTOCOL_ID.to_owned(),
                    contract_address: pump::PROGRAM_ID.to_owned(),
                },
                pool_id: "curve".to_owned(),
            },
            pool_type: "bondingCurve".to_owned(),
            program_id: pump::PROGRAM_ID.to_owned(),
            base_mint: mint.clone(),
            quote_mint: crate::market_state::WRAPPED_SOL_MINT.to_owned(),
            base_decimals: 6,
            quote_decimals: 9,
            base_vault: None,
            quote_vault: None,
            protocol_accounts: Vec::new(),
            support_status: SupportStatus::Supported,
            support_reason: None,
            asset0: None,
            asset1: None,
            validation_block: None,
            validation_block_hash: None,
            fee_tier: None,
            tick_spacing: None,
            bin_step: None,
            hook_address: None,
            pricing_mode: None,
        };
        let mut event = vec![189, 219, 127, 211, 78, 230, 97, 238];
        event.extend_from_slice(&mint_bytes);
        event.extend_from_slice(&2_000_000_000_u64.to_le_bytes());
        event.extend_from_slice(&1_000_000_u64.to_le_bytes());
        event.push(1);

        let mut input = Vec::new();
        write_envelope(&mut input, &Envelope::request("hello", "1", json!({}))).unwrap();
        write_envelope(
            &mut input,
            &Envelope::request(
                "replaceWatchedPools",
                "2",
                json!({ "pools": [{ "descriptor": descriptor, "selectedMint": mint }] }),
            ),
        )
        .unwrap();
        write_envelope(
            &mut input,
            &Envelope::event(
                "rawTransactionUpdate",
                None,
                json!({
                    "signature": "signature",
                    "slot": 20,
                    "failed": false,
                    "commitment": Commitment::Processed,
                    "sourceId": "fixture",
                    "observedAtUnixMs": 200,
                    "instructions": [{
                        "programId": pump::PROGRAM_ID,
                        "dataBase64": BASE64.encode(event),
                        "outerInstructionIndex": 1,
                        "innerInstructionIndex": 2,
                        "stackHeight": 2
                    }]
                }),
            ),
        )
        .unwrap();
        write_envelope(&mut input, &Envelope::request("shutdown", "3", json!({}))).unwrap();

        let mut output = Vec::new();
        run(Cursor::new(input), &mut output).unwrap();
        let mut responses = Cursor::new(output);
        assert_eq!(
            read_envelope(&mut responses).unwrap().unwrap().message_type,
            "helloAck"
        );
        assert_eq!(
            read_envelope(&mut responses).unwrap().unwrap().message_type,
            "requestSucceeded"
        );
        let price = read_envelope(&mut responses).unwrap().unwrap();
        assert_eq!(price.message_type, "priceDelta");
        assert_eq!(
            price.payload["lastTradePriceQuote"]["coefficient"],
            "2000000000000000000"
        );
        assert_eq!(
            read_envelope(&mut responses).unwrap().unwrap().message_type,
            "shutdownAck"
        );
    }
}
