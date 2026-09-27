use crate::domain::Commitment;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RawAccountUpdate {
    pub pubkey: String,
    pub owner_program: String,
    pub data_base64: String,
    pub slot: u64,
    pub write_version: u64,
    pub commitment: Commitment,
    pub source_id: String,
    pub observed_at_unix_ms: i64,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RawTransactionUpdate {
    pub signature: String,
    pub slot: u64,
    pub failed: bool,
    pub commitment: Commitment,
    pub source_id: String,
    pub observed_at_unix_ms: i64,
    pub instructions: Vec<RawInstruction>,
    #[serde(default)]
    pub program_data: Vec<RawProgramData>,
    #[serde(default)]
    pub token_balances: Vec<RawTokenBalance>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RawProgramData {
    pub program_id: String,
    pub data_base64: String,
    pub log_index: u32,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RawInstruction {
    pub program_id: String,
    pub data_base64: String,
    pub outer_instruction_index: u16,
    pub inner_instruction_index: Option<u16>,
    pub stack_height: Option<u32>,
    #[serde(default)]
    pub account_addresses: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RawTokenBalance {
    pub account_address: String,
    pub mint: String,
    pub pre_amount_raw: u64,
    pub post_amount_raw: u64,
}
