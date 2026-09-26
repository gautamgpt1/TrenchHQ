pub const PROGRAM_ID: &str = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Transfer<'a> {
    pub source: &'a str,
    pub destination: &'a str,
    pub amount: u64,
}

pub fn decode_transfer<'a>(data: &[u8], accounts: &'a [String]) -> Option<Transfer<'a>> {
    let (source_index, destination_index) = match data.first().copied()? {
        3 => (0, 1),
        12 => (0, 2),
        _ => return None,
    };
    let amount = u64::from_le_bytes(data.get(1..9)?.try_into().ok()?);
    if amount == 0 {
        return None;
    }
    Some(Transfer {
        source: accounts.get(source_index)?,
        destination: accounts.get(destination_index)?,
        amount,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_transfer_and_transfer_checked() {
        let accounts = vec![
            "source".to_owned(),
            "mint-or-destination".to_owned(),
            "checked-destination".to_owned(),
        ];
        let mut transfer = vec![3];
        transfer.extend_from_slice(&42_u64.to_le_bytes());
        assert_eq!(
            decode_transfer(&transfer, &accounts),
            Some(Transfer {
                source: "source",
                destination: "mint-or-destination",
                amount: 42,
            })
        );

        let mut checked = vec![12];
        checked.extend_from_slice(&84_u64.to_le_bytes());
        checked.push(6);
        assert_eq!(
            decode_transfer(&checked, &accounts),
            Some(Transfer {
                source: "source",
                destination: "checked-destination",
                amount: 84,
            })
        );
    }

    #[test]
    fn rejects_other_token_instructions_and_zero_amounts() {
        assert!(decode_transfer(&[7], &[]).is_none());
        assert!(decode_transfer(&[3, 0, 0, 0, 0, 0, 0, 0, 0], &[]).is_none());
    }
}
