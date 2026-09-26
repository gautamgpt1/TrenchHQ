use thiserror::Error;

pub const PROGRAM_ID: &str = "Eo7WjKq67rjJQSZxS6z3YkapzY3eMj6Xy8X5EQVn5UaB";
const POOL_ACCOUNT_LENGTHS: [usize; 3] = [944, 952, 1387];
const POOL_DISCRIMINATOR: [u8; 8] = [241, 154, 109, 4, 17, 177, 109, 188];
const SWAP_DISCRIMINATOR: [u8; 8] = [248, 198, 158, 145, 225, 117, 135, 200];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CurveType {
    ConstantProduct,
    Stable,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MeteoraDammV1PoolState {
    pub token_a_mint: String,
    pub token_b_mint: String,
    pub a_vault: String,
    pub b_vault: String,
    pub a_vault_lp: String,
    pub b_vault_lp: String,
    pub enabled: bool,
    pub curve_type: CurveType,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum MeteoraDammV1DecodeError {
    #[error("account owner is not the Meteora DAMM v1 program")]
    WrongOwner,
    #[error("account length is not a known Meteora DAMM v1 Pool allocation")]
    WrongLength,
    #[error("account discriminator is not a Meteora DAMM v1 Pool")]
    WrongDiscriminator,
    #[error("Meteora DAMM v1 enabled flag is invalid")]
    InvalidEnabledFlag,
    #[error("Meteora DAMM v1 curve type is unsupported")]
    InvalidCurveType,
}

pub fn is_swap_instruction(data: &[u8]) -> bool {
    data.starts_with(&SWAP_DISCRIMINATOR)
}

pub fn decode_pool(
    owner_program: &str,
    data: &[u8],
) -> Result<MeteoraDammV1PoolState, MeteoraDammV1DecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(MeteoraDammV1DecodeError::WrongOwner);
    }
    if !POOL_ACCOUNT_LENGTHS.contains(&data.len()) {
        return Err(MeteoraDammV1DecodeError::WrongLength);
    }
    if data[..8] != POOL_DISCRIMINATOR {
        return Err(MeteoraDammV1DecodeError::WrongDiscriminator);
    }
    let enabled = match data[233] {
        0 => false,
        1 => true,
        _ => return Err(MeteoraDammV1DecodeError::InvalidEnabledFlag),
    };
    let curve_type = match data[874] {
        0 => CurveType::ConstantProduct,
        1 => CurveType::Stable,
        _ => return Err(MeteoraDammV1DecodeError::InvalidCurveType),
    };

    Ok(MeteoraDammV1PoolState {
        token_a_mint: pubkey(data, 40),
        token_b_mint: pubkey(data, 72),
        a_vault: pubkey(data, 104),
        b_vault: pubkey(data, 136),
        a_vault_lp: pubkey(data, 168),
        b_vault_lp: pubkey(data, 200),
        enabled,
        curve_type,
    })
}

fn pubkey(data: &[u8], offset: usize) -> String {
    bs58::encode(&data[offset..offset + 32]).into_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_all_observed_pool_allocations_and_swap() {
        for length in POOL_ACCOUNT_LENGTHS {
            let mut data = vec![0_u8; length];
            data[..8].copy_from_slice(&POOL_DISCRIMINATOR);
            data[40..72].copy_from_slice(&[1; 32]);
            data[72..104].copy_from_slice(&[2; 32]);
            data[104..136].copy_from_slice(&[3; 32]);
            data[136..168].copy_from_slice(&[4; 32]);
            data[168..200].copy_from_slice(&[5; 32]);
            data[200..232].copy_from_slice(&[6; 32]);
            data[233] = 1;

            let state = decode_pool(PROGRAM_ID, &data).unwrap();
            assert!(state.enabled);
            assert_eq!(state.curve_type, CurveType::ConstantProduct);
            assert_eq!(state.a_vault, bs58::encode([3_u8; 32]).into_string());
        }
        assert!(is_swap_instruction(&SWAP_DISCRIMINATOR));
        assert!(!is_swap_instruction(&[1; 8]));
    }

    #[test]
    fn distinguishes_stable_pools_and_rejects_invalid_layouts() {
        let mut data = vec![0_u8; 944];
        data[..8].copy_from_slice(&POOL_DISCRIMINATOR);
        data[233] = 1;
        data[874] = 1;
        assert_eq!(
            decode_pool(PROGRAM_ID, &data).unwrap().curve_type,
            CurveType::Stable
        );
        assert_eq!(
            decode_pool("wrong", &data),
            Err(MeteoraDammV1DecodeError::WrongOwner)
        );
        assert_eq!(
            decode_pool(PROGRAM_ID, &data[..900]),
            Err(MeteoraDammV1DecodeError::WrongLength)
        );
        data[233] = 2;
        assert_eq!(
            decode_pool(PROGRAM_ID, &data),
            Err(MeteoraDammV1DecodeError::InvalidEnabledFlag)
        );
    }
}
