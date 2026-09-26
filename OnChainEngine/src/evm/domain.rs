use crate::domain::PoolKey;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BlockReference {
    pub number: u64,
    pub hash: String,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HeadUpdate {
    pub chain_id: String,
    pub connection_epoch: u64,
    pub number: u64,
    pub hash: String,
    pub parent_hash: String,
    pub timestamp: u64,
    pub observed_at_unix_ms: i64,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct LogUpdate {
    pub chain_id: String,
    pub connection_epoch: u64,
    pub address: String,
    pub topics: Vec<String>,
    pub data: String,
    pub block_number: u64,
    pub block_hash: String,
    pub transaction_hash: String,
    pub transaction_index: u64,
    pub log_index: u64,
    pub removed: bool,
    pub observed_at_unix_ms: i64,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SnapshotCall {
    pub id: String,
    pub success: bool,
    pub return_data: String,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SnapshotResponse {
    pub request_id: String,
    pub pool_key: PoolKey,
    pub block_number: u64,
    pub block_hash: String,
    pub calls: Vec<SnapshotCall>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FinalityUpdate {
    pub chain_id: String,
    pub head: BlockReference,
    pub safe: Option<BlockReference>,
    pub finalized: Option<BlockReference>,
    pub observed_at_unix_ms: i64,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Rollback {
    pub chain_id: String,
    pub to_block_number: u64,
    pub to_block_hash: String,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::domain::DeploymentKey;
    use serde_json::json;

    #[test]
    fn evm_log_contract_round_trips_exact_identity() {
        let expected = LogUpdate {
            chain_id: "1".to_owned(),
            connection_epoch: 7,
            address: "0x1111111111111111111111111111111111111111".to_owned(),
            topics: vec!["0xabc".to_owned()],
            data: "0x00".to_owned(),
            block_number: 22,
            block_hash: "0xblock".to_owned(),
            transaction_hash: "0xtx".to_owned(),
            transaction_index: 3,
            log_index: 4,
            removed: false,
            observed_at_unix_ms: 1_700_000_000_000,
        };

        let encoded = serde_json::to_value(&expected).unwrap();
        let actual: LogUpdate = serde_json::from_value(encoded).unwrap();
        assert_eq!(actual, expected);
    }

    #[test]
    fn evm_contracts_reject_unknown_fields() {
        let value = json!({
            "chainId": "1",
            "connectionEpoch": 1,
            "number": 2,
            "hash": "0x02",
            "parentHash": "0x01",
            "timestamp": 3,
            "observedAtUnixMs": 4,
            "unexpected": true
        });

        assert!(serde_json::from_value::<HeadUpdate>(value).is_err());
    }

    #[test]
    fn snapshot_uses_deployment_and_pool_id() {
        let snapshot = SnapshotResponse {
            request_id: "snapshot-1".to_owned(),
            pool_key: PoolKey {
                deployment_key: DeploymentKey {
                    chain_namespace: "eip155".to_owned(),
                    chain_id: "1".to_owned(),
                    protocol_id: "uniswap-v3".to_owned(),
                    contract_address: "0x1F98431c8aD98523631AE4a59f267346ea31F984".to_owned(),
                },
                pool_id: "0x88e6A0c2dDD26FEEb64F039a2c41296FcB3f5640".to_owned(),
            },
            block_number: 100,
            block_hash: "0x100".to_owned(),
            calls: vec![],
        };

        let encoded = serde_json::to_value(snapshot).unwrap();
        assert_eq!(encoded["poolKey"]["deploymentKey"]["chainId"], "1");
        assert_eq!(
            encoded["poolKey"]["poolId"],
            "0x88e6A0c2dDD26FEEb64F039a2c41296FcB3f5640"
        );
    }
}
