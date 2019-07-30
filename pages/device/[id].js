import Layout from '../../components/Layout';
import dataservice from '../../services/dataservice';
import formatters from '../../services/formatters';

const Post = props => (
    <Layout>
        <h1>Device {props.mac}</h1>
        <ul>
            {props.leases.map(lease => (
            <li key={lease.id}>
                {lease.ip} - {lease.host} - {formatters.toSmallDateTime(lease.start)} - {formatters.toSmallDateTime(lease.end)}
            </li>
            ))}
        </ul>
  </Layout>
);

Post.getInitialProps = async function(context) {
  const { id } = context.query;
  const data = await dataservice.getLeasesByMac(id);

  console.log(`Fetched device: ${id}`);

  return { mac: id, leases: data };
};

export default Post;